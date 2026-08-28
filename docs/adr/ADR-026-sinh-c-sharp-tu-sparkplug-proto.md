# ADR-026 — Sinh C# từ `sparkplug_b.proto` đã vendored, không dùng thư viện Sparkplug

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `docs/plans/M2-simulator-ingestion-idempotency.md` §2.2, §3.2, §5.C01 · `docs/scope.md` §7.1 · ADR-021 (bài học kiểm license trước khi pin) · AGENTS.md §4/K6 |

---

## Context

`scope.md` §7.1 để ngỏ đúng một câu: *"MQTTnet không decode Sparkplug — cần thư viện `SparkplugNet`
hoặc tự sinh code từ `sparkplug_b.proto`."* M2 là milestone đầu tiên phải chọn, vì C02 trở đi không
viết được dòng nào nếu chưa có kiểu để decode ra.

**Ràng buộc thật:**

| | |
|---|---|
| Thứ phải đọc được | Payload Sparkplug B của thiết bị formation. Protobuf nhị phân, không phải JSON |
| Kích thước bài toán | `sparkplug_b.proto` là **8.330 byte**, 224 dòng. Không phải một đặc tả khổng lồ |
| Cú pháp | **`proto2`** — và C# **không phải lúc nào cũng** sinh được từ proto2. Đây là điều bắt buộc phải đo trước khi quyết, không được giả định |
| Bài học còn nóng | M1/C09: MassTransit v9 đổi sang license thương mại, phát hiện **sau** khi định pin. Trước đó là FluentAssertions v8 ở M0/C04. Kiểm license **trước** khi pin, hai lần đã trả giá |
| Thứ M2 tồn tại để học | `bdSeq`, `seq` nhảy cóc, rebirth, bảng alias theo node — `scope.md` §7.1 và C11 |
| K6 | Contract phải đọc được 10 năm sau. Payload thiết bị không nằm trong `Nvm.Contracts`, nhưng cùng loại rủi ro: định dạng do **bên ngoài** quy định, ta không có quyền sửa |

Điểm cuối của bảng là chỗ dễ bị bỏ qua nhất, và nó đổi hẳn hình dạng của quyết định: **file `.proto`
không phải code của dự án này, nó là đặc tả.** Thiết bị trên sàn nhà máy encode theo nó. Chỗ duy
nhất ta có quyền quyết là *đọc nó bằng đường nào*.

Không quyết bây giờ: `Nvm.Sparkplug` không tồn tại, và C02 → C19 của M2 đứng.

## Decision

**Vendor `sparkplug_b.proto` của Eclipse Tahu vào repo và sinh C# lúc build** bằng
`Google.Protobuf` 3.35.1 (runtime) + `Grpc.Tools` 2.83.0 (protoc). Không dùng `SparkplugNet`.

Bốn ràng buộc đi kèm, cả bốn ép bằng test (`SparkplugPinTests`), không bằng comment:

1. File `.proto` được **commit**, không tải lúc build.
2. Body của nó bị pin bằng **SHA-256**; header xuất xứ ta thêm vào nằm ngoài phần băm.
3. Runtime và protoc pin về **cùng một commit** của protobuf.
4. Code sinh ra **không** được commit — nó phải luôn là sản phẩm của file `.proto` đang nằm trong repo.

**Không thuộc quyết định này**: vòng đời `bdSeq`, phát hiện `seq` nhảy cóc, yêu cầu rebirth, và
bảng alias theo edge node. Đó là **nghiệp vụ**, thuộc C11, và §Consequences nói vì sao không giao
chúng cho thư viện.

## Consequences

**Được**

- **Đọc đúng thứ thiết bị viết.** Bản `.proto` của Tahu *là* đặc tả, không phải một diễn giải của
  nó. Sinh từ đó thì không có tầng nào ở giữa để hiểu sai.
- **Bề mặt phụ thuộc nhỏ.** Đúng một package runtime (`Google.Protobuf`) và một package build-time.
  Không kéo theo MQTT client, không kéo theo một implementation protobuf thứ hai.
- **Phần đáng học ở lại trong repo.** `bdSeq`, `seq`, rebirth là câu trả lời cho *"máy nào đang
  sống, và tôi đã bỏ lỡ những gì"* — câu hỏi của người vận hành lúc 3 giờ sáng, không phải của lập
  trình viên. Giao nó cho thư viện là giao đi đúng phần mà lúc sự cố ta cần đọc được.
- **Không có drift giữa `.proto` và `.cs`**, vì `.cs` không tồn tại trong repo.
- **License sạch, kiểm trước khi pin** (§Evidence): EPL-2.0 cho file `.proto`, BSD-3-Clause cho
  `Google.Protobuf`, Apache-2.0 cho `Grpc.Tools`.

**Mất / phải chịu**

- **Ta sở hữu phần vòng đời.** Ghép cặp birth–death theo `bdSeq`, bảng alias theo node, phát hiện
  `seq` nhảy cóc, gửi rebirth — `SparkplugNet` đã có sẵn và đã gặp production; code của ta thì chưa.
  Đây là cái giá đắt nhất của quyết định này và nó **không** được trả bằng lập luận, nó phải được
  trả bằng test ở C11.
- **Namespace lạ trong code của mình**: type sinh ra nằm ở `Org.Eclipse.Tahu.Protobuf`, không phải
  `Nvm.*`. Đổi được bằng `option csharp_namespace`, nhưng làm vậy là sửa file vendored và mọi lần
  cập nhật upstream lại phải vá tay đúng dòng đó.
- **Bản copy không tự mới.** Upstream sửa gì thì repo này không biết. Digest làm cho việc "copy đã
  cũ" **hiện ra khi ai đó cập nhật**, nhưng không nhắc ai đi cập nhật.
- **`proto2` là đường ít người đi của bộ sinh code C#.** Hệ sinh thái C# của protobuf lớn lên quanh
  `proto3`; `sparkplug_b.proto` thì là `proto2`. Đã **đo** là protoc 35.1 sinh được và code sinh ra
  biên dịch sạch (§Evidence) — nhưng đó là một phép đo tại một version, không phải một lời hứa. Bản
  protoc về sau đổi ý thì đó là việc repo này phải xử lý, không phải của một vendor.
- **Hai package phải đi cùng nhau.** Nâng `Google.Protobuf` mà quên `Grpc.Tools` là tạo skew giữa
  runtime và protoc. Ép bằng test, nhưng vẫn là một luật phải nhớ khi đọc dependabot PR.
- **Fixture phải đến từ nơi khác.** Test decode không được dùng payload do chính code này encode ra,
  nên `tests/Fixtures/sparkplug/` phải có một toolchain khác (Python) đứng sau — thêm một thứ trong
  repo mà `make ci` không chạy.

**Việc phát sinh**

- `SparkplugPinTests` là một phần của quyết định, không phải phụ kiện: cập nhật `.proto` thì cập
  nhật digest **trong cùng commit**, và commit đó phải nói vì sao.
- **C11** chịu trách nhiệm phần vòng đời nêu ở trên. Nếu tới C11 mà phần đó bị cắt cho kịp thời
  gian, quyết định này mất phần lớn lý do tồn tại — mở lại ADR thay vì để nó đứng.
- **C02** ánh xạ `Payload` sang kiểu của mình. Kiểu sinh ra **không** được rò ra khỏi
  `Nvm.Sparkplug` — `Org.Eclipse.Tahu.Protobuf.Payload` xuất hiện trong chữ ký của FB nào là dấu
  hiệu ranh giới đã thủng.
- Khi `Grpc.Tools` ra bản mang protoc mới: nâng `Google.Protobuf` về **đúng commit tương ứng**
  trong cùng commit git, và cập nhật hằng số trong `SparkplugPinTests`.

**Khi nào mở lại quyết định này**: khi phần vòng đời ở C11 tốn nhiều hơn ~2 ngày công và vẫn chưa
đúng, hoặc khi cần Sparkplug **phía phát** (giả lập một edge node thật, không chỉ đọc) — lúc đó cán
cân giữa "tự viết" và "dùng thư viện" đổi thật, không phải đổi vì mệt.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **`SparkplugNet` 1.4.1** | Xem phân tích riêng bên dưới |
| **Tự viết parser protobuf bằng tay** | Wire format protobuf không khó, nhưng viết lại nó là viết lại một thứ đã đúng, và không học được gì thuộc về MES. Đặt công sức sai chỗ — cùng lập luận đã dùng cho EF Core ở M1/§3.1 |
| **Tải `.proto` lúc build** | Build chạm mạng là build hỏng khác nhau vào sáng thứ Hai. Và byte mà protoc biên dịch sẽ không phải byte ai đó đã review |
| **Bỏ Sparkplug, dùng JSON cho tiện** | `scope.md` §7.1 đã cấm thẳng, và lý do là nghiệp vụ chứ không phải khẩu vị: `NBIRTH`/`NDEATH`/`bdSeq` **là** cơ chế phát hiện mất kết nối, và N15 cần nó. Dùng JSON là bỏ đúng phần khó |
| **Đổi `package` trong `.proto` để có namespace `Nvm.*`** | Sửa file vendored. Một dòng hôm nay, một lần vá tay mỗi lần cập nhật upstream, mãi mãi |

### Vì sao loại `SparkplugNet` — nói cho đủ

Lý do **không phải** license và **không phải** chất lượng. Kiểm 2026-08-28: `SparkplugNet` 1.4.1 là
**MIT**, có target `net10.0`, còn được bảo trì. Không có bẫy nào như MassTransit v9.

Ba lý do thật, xếp theo sức nặng:

1. **Nó quyết hộ ba thứ cùng lúc.** Dependency của bản `net10.0`: `MQTTnet 5.2.0.1603`,
   `protobuf-net 3.3.21`, `Microsoft.Extensions.Logging.Abstractions 10.0.11`. Nghĩa là chọn thư
   viện Sparkplug cũng là chọn **MQTT client** và chọn **implementation protobuf** cho cả repo —
   ba quyết định trong một, và hai trong ba chưa tới lúc phải quyết.
2. **Phần nó làm hộ đúng là phần M2 tồn tại để làm.** Thứ `SparkplugNet` mang lại nhiều hơn decode
   là mô hình edge node / application: theo dõi `bdSeq`, giữ bảng alias, phát rebirth. `scope.md`
   §7.1 và C11 đặt đúng ba thứ đó làm nội dung học. Giao đi thì decode xong sớm hơn một buổi và
   không hiểu thêm điều gì.
3. **Lúc sự cố, ta phải đọc được chỗ đó.** Câu hỏi *"vì sao kênh FORM-01-CH-0142 im 40 phút mà
   không ai báo"* được trả lời trong đúng phần vòng đời. Một triển khai của người khác thì phải đọc
   source của họ trước, giữa lúc dây chuyền đang chạy.

Lý do **thứ tư và yếu nhất**: thêm một layer giữa ta và đặc tả. Ghi ra để nếu ba lý do trên mất giá
trị — ví dụ M2 phải cắt và C11 bị bỏ — thì người đọc biết rằng lý do còn lại không đủ để giữ quyết
định này.

## Evidence

**1. protoc sinh được C# từ proto2** — phải đo chứ không được giả định: bộ sinh code C# của
protobuf lớn lên quanh `proto3`, còn `sparkplug_b.proto` khai `syntax = "proto2"` ngay dòng đầu.
Nếu đường này không đi được thì cả quyết định đổ, nên nó là phép đo **trước** khi viết một dòng nào.

```
$ ~/.nuget/packages/grpc.tools/2.83.0/tools/windows_x64/protoc.exe --version
libprotoc 35.1

$ dotnet build src/Platform/Nvm.Sparkplug/Nvm.Sparkplug.csproj --nologo
  Nvm.Sparkplug -> artifacts/bin/Nvm.Sparkplug/debug/Nvm.Sparkplug.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

0 warning là con số đáng chú ý riêng: repo bật `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`,
và code sinh ra vẫn sạch vì protoc phát `// <auto-generated>` kèm `#pragma warning disable 1591`.

**2. Runtime và protoc là cùng một commit** — cơ sở của luật pin ở `Directory.Packages.props`:

```
grpc.tools 2.83.0            → libprotoc 35.1
nuspec google.protobuf 3.35.1 → repository commit 35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03
protobuf tag v35.1            → cùng commit  35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03
Google.Protobuf.dll → InformationalVersion = 3.35.1+35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03
```

**3. License, kiểm trước khi pin** (nuspec, 2026-08-28):

```
google.protobuf 3.35.1 → <license type="expression">BSD-3-Clause</license>
grpc.tools      2.83.0 → <license type="expression">Apache-2.0</license>
sparkplugnet    1.4.1  → <license type="expression">MIT</license>       (phương án bị loại)
sparkplug_b.proto      → SPDX-License-Identifier: EPL-2.0               (trong chính file)
```

**4. Xuất xứ file `.proto`**:

```
eclipse-tahu/tahu @ 46f25e79f34234e6145d11108660dfd9133ae50d  (2022-05-16, không đổi từ đó)
sparkplug_b/sparkplug_b.proto — 8.330 byte, LF
sha256 = 4432c5c483b7fb9732d0594c98a2e97dca5e517e39c5374a8b918d837f0b4a19
```

**5. Lab phá hoại — vì sao digest tồn tại.** Sửa **một** field number trong file vendored
(`Metric.alias` từ `2` thành `20`), đúng kiểu một lần "dọn dẹp" vô hại:

| | |
|---|---|
| `dotnet build` | **0 error, 0 warning** — compiler không có gì để nói |
| `dotnet test` | **5 / 290 đỏ** |
| Triệu chứng ở DDATA | `metric.HasAlias` = `False`, mọi alias decode ra `0`; payload **parse thành công** |
| Triệu chứng ở phép join birth ↔ data | `ArgumentException: An item with the same key has already been added. Key: 0` |

Đây là toàn bộ lý do có `SparkplugPinTests`: sai một số trong đặc tả thì protobuf đọc nó thành
*unknown field* rồi đi tiếp — **không exception, không log, không cảnh báo build**. Cả 5 test đỏ đều
là test của C01, và ba trong số đó là test decode payload thật, nên phép kiểm không phụ thuộc riêng
vào digest.

Số này có một dòng trong `docs/benchmarks.md` §M2.

**6. Fixture đọc được bằng công cụ độc lập**: `protoc --decode` trên hai file `.bin` do
`pysparkplug` 0.6.1 sinh ra cho đúng nội dung mong đợi — 5 metric ở `DBIRTH`, 2 metric chỉ có alias
ở `DDATA`. Output đầy đủ ở `tests/Fixtures/sparkplug/README.md`.

**7. Chưa đo**: hiệu năng decode. C01 không chạm tới nó và **phán đoán** là nó không nằm gần nút
thắt của N1 (5.000 msg/s) — nhưng đó là phán đoán, không phải phép đo. Đo ở **C16** cùng load
harness, ghi vào `benchmarks.md`. Nếu sai, ADR này không đổi: nút thắt sẽ ở I/O hoặc ở dedup, không
ở protobuf.
