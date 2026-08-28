# ADR-010 — Idempotency key là UUIDv5 suy ra từ natural key

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/scope.md` §7.2 · `docs/plans/M1-factory-model-bus.md` §C04.1, §C04.2 · ADR-020 (globalization) · AGENTS.md K7 |

---

## Context

Thiết bị trong nhà máy gửi lại khi không nhận được ack. Edge gateway mất kết nối rồi khôi phục sẽ
flush cả cụm dữ liệu tồn. RabbitMQ là at-least-once. Ba nguồn khác nhau, cùng một hệ quả: **một phép
đo sẽ đến 2–3 lần, và đó là vận hành bình thường chứ không phải sự cố** (`scope.md` §7.2).

Ràng buộc cụ thể đang phải đáp ứng:

- **N4**: 0 bản ghi thừa với 10% duplicate, đo ở M2.
- **K7**: mọi command handler idempotent.
- Dedup xảy ra ở **hai tầng** — ingestion (chặn duplicate từ thiết bị) và command handler (bus cũng
  at-least-once). Hai tầng chỉ nối được vào nhau nếu **khoá giống nhau**.
- Event store giữ **15 năm**. Khoá phải cho cùng một giá trị sau 15 năm, trên máy khác, ngôn ngữ
  khác — kể cả khi người kiểm tra tự tính lại bằng Python.

Nếu không quyết bây giờ: `ICommand` không khai được `IdempotencyKey`, và pipeline behavior ở C05
không có gì để tra. Toàn bộ M2 đứng.

Điều đã biết lúc quyết: .NET 10 có `Guid.CreateVersion7()` và **không có** version 5.

## Decision

Khoá idempotency là **UUID version 5** (RFC 4122) sinh từ `(namespace, natural key)`.

- Namespace gốc `NovaVoltNamespace` **không phải GUID ngẫu nhiên hard-code**. Nó là
  `uuidv5(DNS_namespace, "novavolt.example")` = `40e49ff3-f5f7-58a6-85aa-d4db02fa35ce` — ai cũng
  tính lại được.
- Natural key được mã hoá **injective** trước khi băm: mỗi phần ghi thành `<độ dài>:<giá trị>|`.
- `Guid.Empty` bị từ chối ở `IdempotencyKey.From`.

**Không** thuộc quyết định này: bảng dedup ở tầng ingestion (M2), và behavior tra khoá trong
pipeline (C05). ADR này chỉ chốt **cách sinh ra giá trị**.

## Consequences

**Được**

- Ba lần gửi cùng một phép đo cho **cùng một khoá**, ở process khác, máy khác, ba ngày sau.
- Hai tầng dedup khoá theo cùng một giá trị → ghép được thành một cơ chế.
- Kiểm chứng lại được bằng công cụ ngoài .NET. Auditor không phải tin lời ai.

**Mất / phải chịu**

- **Phải tự viết ~35 dòng** vì BCL không có v5. Tự viết nghĩa là tự chịu trách nhiệm với thứ tự
  byte và hai dòng set bit version/variant — cả hai đều hỏng im lặng nếu sai.
- **Phải tắt CA5350 tại chỗ**. SHA-1 là thuật toán yếu; ở đây nó chỉ trộn bit, không bảo vệ gì.
  Suppress bằng `[SuppressMessage]` **ngay tại method**, không tắt cả repo.
- **Chi phí băm mỗi lần sinh khoá.** SHA-1 trên chuỗi ngắn là không đáng kể ở 5.000 msg/s, nhưng
  đây là phán đoán chứ chưa đo — đo ở M2 cùng N1.
- **Natural key trở thành một phần của contract.** Đổi thứ tự hay thêm bớt một trường trong natural
  key sẽ đổi mọi khoá sinh ra sau đó, và dữ liệu cũ trong bảng dedup không còn khớp với dữ liệu mới.
  Đổi natural key = một cuộc migration, không phải một lần refactor.

**Việc phát sinh**

- Test pin cứng giá trị `c82fd38e-f3ec-5601-a915-1c8af2eb00a9` cho natural key mẫu của một phép đo
  OCV. Test này đỏ nghĩa là khoá đã đổi — và khoá đổi là sự cố dữ liệu, không phải test hỏng.
- Test kiểm **nibble version = 5** và **variant RFC 4122**, vì quên hai dòng bit vẫn cho ra khoá
  dedup đúng.
- Ở C12, `ce_id` của event sinh từ command **phải bằng** `IdempotencyKey` của command đó.
- Ở M6, khi outbox lưu khoá xuống SQL Server: **đọc §C04.2 trước** — xem mục Evidence.

### Rủi ro đã biết: đồng hồ chạy nhanh chiếm chỗ của phép đo thật đến sau

Khoá tự nhiên gồm `device_timestamp` và **không** gồm `clock_quality`. Hệ quả trên dây chuyền thật:
một kênh có đồng hồ chạy nhanh ghi vào **tương lai**, và khi thời gian thật đi tới đúng mili-giây đó,
phép đo THẬT mang cùng natural key sẽ bị dedup **nuốt im lặng**. Không có lỗi nào được ném, không
counter nào tăng ngoài `duplicates`.

Đo được ở M2 (2026-08-29): **307.104** row có `device_timestamp` tới tận **2026-09-10** do simulator
nén thời gian, và **4.149** row va chạm trong một lần chạy D2 — toàn bộ là `Drifted`, toàn bộ row của
run là `Good`. Ở lab đó nó vô hại vì nguồn là simulator; với thiết bị thật thì không.

**Không sửa bằng cách thêm `gateway_timestamp` hoặc `clock_quality` vào khoá.** Cả hai đổi giá trị khi
message được gửi lại hoặc replay, nên thêm chúng là làm hỏng đúng tính chất khoá này tồn tại để có:
cùng một phép đo, gửi ba lần, ba ngày sau, vẫn ra một khoá.

| | |
|---|---|
| **Trạng thái** | Chấp nhận có ý thức ở M2. Không có sample id đáng tin từ thiết bị để đưa vào khoá |
| **Địa chỉ** | **Trước khi có thiết bị thật đầu tiên** — nghĩa là trước milestone đầu tiên đọc dữ liệu từ máy thật, không phải "một lúc nào đó" |
| **Acceptance gate lúc đó** | Hoặc thiết bị cấp một sample id ổn định để thay `device_timestamp` trong khoá, hoặc ingestion phải **đếm và báo** va chạm giữa một row `Drifted` có sẵn và một row `Good` đến sau, thay vì để `duplicates` gộp chung |

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **UUIDv7** | Không deterministic. Chứa timestamp lúc gọi hàm, mà lúc gọi hàm chính là thứ khác nhau giữa hai lần gửi. Về bản chất không làm được việc này — dù nó "mới hơn" |
| **UUIDv4** | Như v7, và không có cả tính sắp xếp |
| **UUIDv3** | Cùng cơ chế, nhưng MD5 thay SHA-1. Không có lý do gì chọn bản yếu hơn khi cả hai đều không dùng cho bảo mật |
| **Khoá là chính chuỗi natural key** (không băm) | Chuỗi dài, độ dài thay đổi, không index tốt, và lộ dữ liệu nghiệp vụ ra mọi nơi khoá đi qua |
| **Băm SHA-256 rồi cắt 128 bit** | Deterministic và mạnh hơn, nhưng **không phải UUID** — Postgres `uuid`, CloudEvents `id` và công cụ của auditor đều mong một UUID hợp lệ. Chuẩn thắng "tốt hơn" ở chỗ này |
| **`string.Join('|', parts)` rồi băm** | Không injective. Xem Evidence — hai natural key khác nhau ra cùng một khoá |

## Evidence

**1. v7 không dùng được, v5 dùng được** — chạy 2026-08-26 trên .NET 10:

```
== v7: goi 2 lan, cung mot thoi diem ==
  #1 01a03e70-6be0-794f-8991-c6f26cb2ec16
  #2 01a03e70-6be0-7008-9b56-ef5c173df26b
  bang nhau? False

== v5: goi 2 lan tu CUNG mot natural key ==
  #1 9df0deb6-2bef-522b-bf31-e4479213c66a
  #2 9df0deb6-2bef-522b-bf31-e4479213c66a
  bang nhau? True
```

**2. Bản cài đặt khớp implementation độc lập.** Đối chiếu với `uuid.uuid5` của Python:

```
$ python -c "import uuid; [print(uuid.uuid5(uuid.NAMESPACE_DNS, n)) for n in
              ['www.example.com','python.org','novavolt.example']]"
2ed6657d-e927-568b-95e1-2665a8aea6a2
886313e1-3b8a-5372-9b90-0c9aee199e5d
40e49ff3-f5f7-58a6-85aa-d4db02fa35ce      <- NovaVoltNamespace
```

Cả ba khớp giá trị C# sinh ra. Đây là bằng chứng cho vế *"auditor tính lại được"*, chứ không phải
bằng chứng code tự nhất quán với chính nó.

**3. Mã hoá injective là bắt buộc, không phải trang trí.** Bỏ tiền tố độ dài (tức là dùng
`string.Join`) rồi chạy test:

```
failed  FromNaturalKey_PartsThatWouldFlattenToTheSameString_StillProduceDifferentKeys
failed  FromNaturalKey_KnownFact_ProducesAStableValueAcrossRunsAndMachines
```

Test đầu là tình huống thật: `["NV1", "ROL|004", "STACK"]` và `["NV1", "ROL", "004|STACK"]` cùng
dẹt thành `"NV1|ROL|004|STACK"`. **Hai phép đo khác nhau, một khoá — và một cái biến mất ở bước
dedup.** Lot code của nhà cung cấp là chuỗi tự do từ hệ thống của người khác, nên ký tự phân cách
lọt vào giá trị là chuyện *khi nào*, không phải *có hay không*.

**5. Khoá này chặn bao nhiêu — lab phá hoại #1 của `scope.md` §9/M2.** Đo ở M2, chỗ trống mà
ADR này để lại từ M1/C04 giờ điền được.

`make reconcile DURATION=300` trên đường ống thật, simulator bật đủ fault, `TimeCompression=1`:

```
  Phep do logic simulator sinh ra   : 282
  Row telemetry trong DB            : 282
  Lech                              : 0
  Duplicate bi dedup chan           : 69
```

**69 trên 282 row — 24,5 %.** Mỗi delivery rơi vào `ON CONFLICT DO NOTHING` là đúng một row sẽ
thừa nếu khoá này không tồn tại. Cao hơn 10 % mà fault của simulator bơm vào, vì duplicate còn đến
từ hai nguồn nữa: MQTT QoS 1 giao lại, và rebirth khai lại nguyên trạng cả kênh (`C11`).

Điều đáng nhớ không phải con số. Là **cách nó hỏng nếu thiếu**: không exception, không log lỗi,
`INSERT` nào cũng thành công. Bảng chỉ đơn giản có nhiều hơn 24,5 % số phép đo mà nhà máy đã đo, và
mọi phép tính yield trên đó đều lệch — theo một hướng khó ngờ, vì dữ liệu *thừa* trông không giống
dữ liệu hỏng.

Lab **#2** đo chiều ngược lại — bỏ `device_timestamp` khỏi khoá thì **65.628 / 65.676 phép đo
(99,93 %) bị nuốt**, và cũng im lặng y hệt. Số ở `benchmarks.md`; phép đo ở
`NaturalKeyWithoutDeviceTimestampTests`.

**4. Cái bẫy cho M6 — UUIDv7 trên SQL Server không hề sequential.** Chạy trên container
`nvm-mssql` ngày 2026-08-26:

```sql
ORDER BY id  -->
  01000000-0000-0000-0000-000000000000     byte dau  = 01
  FF000000-0000-0000-0000-000000000000     byte dau  = FF
  00000000-0000-0000-0000-000000000001     byte cuoi = 01   <- dung CUOI
```

SQL Server so kiểu `uniqueidentifier` theo **6 byte cuối trước**. v7 giấu timestamp ở 6 byte
**đầu**, nên lưu v7 vào cột `uniqueidentifier` vẫn chèn ngẫu nhiên y hệt v4 — page split, phân
mảnh index, và **không có gì báo**. Liên quan tới ADR này vì M6 sẽ dùng v7 cho khoá thay thế của
bảng outbox. Ba cách xử lý: lưu `binary(16)`, hoán vị byte, hoặc `NEWSEQUENTIALID()` cho khoá
clustered.

**5. Phán đoán chưa đo.** Chi phí SHA-1 ở 5.000 msg/s được **giả định** là không đáng kể. Chưa đo.
Đo ở M2 cùng N1 và ghi vào `benchmarks.md`.
