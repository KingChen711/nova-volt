# ADR-022 — Publish thẳng lên bus ở M1, chấp nhận mất event khi broker chết

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-27 |
| **Liên quan** | ADR-001 (event store trên SQL Server), ADR-004, `docs/scope.md` §5.5 (bẫy dual-write), §9/M1, §9/M6, `docs/plans/M1-factory-model-bus.md` §3.3 và §5.C13.3 |

---

## Context

M1 dựng Manufacturing Service Bus. Command handler sinh ra event, và ai đó phải đưa event lên bus.
Ở C13 việc đó nằm ở phía gọi: dispatch command, nhận event, `Publish` — **hai bước, không có gì nối
chúng lại**.

Ràng buộc thật lúc quyết định:

- **Chưa có chỗ nào để ghi event xuống trước khi publish.** Event store bắt đầu ở **M5**
  (`scope.md` §9/M5, ADR-001) và schema của nó chưa chốt; transactional outbox nối bảng đó với
  RabbitMQ là việc của **M6**. Không có bảng thì không có transaction, không có transaction thì
  không có outbox — nên hai mốc này nối tiếp nhau chứ không thay nhau.
- **N3 nói "0 message mất sau outage 2 phút"** và được gán cho M13. **N15** — MES chết không được
  làm dừng dây chuyền — được gán cho M2. Cả hai đều chưa tới hạn ở M1.
- `scope.md` §9/M1 lúc đầu viết lab phá hoại của M1 là *"tắt RabbitMQ giữa lúc publish → producer
  phải buffer/retry, **không mất event**, không crash"*. Mệnh đề đó **không thể đạt** ở M1: khi broker
  tắt, `Publish` thất bại và event chỉ còn tồn tại trong bộ nhớ process.

Không quyết thì bị chặn hai việc: (1) không biết phát biểu DoD của M1 thế nào cho đo được, và (2)
không có cơ sở nào để trả lời câu hỏi ở M6 — *"outbox thêm một bảng, một worker và một transaction,
đổi lại được gì?"*

## Decision

Ở M1, App **publish thẳng lên bus ngay sau khi command handler trả về**, không qua bảng trung gian.
Chấp nhận rằng broker chết trong lúc publish thì event **mất**, và **đo con số đó** thay vì giả vờ
nó bằng 0.

Ranh giới của quyết định này:

- Nó **không** nói outbox là không cần. Nó nói outbox thuộc M6, cùng lúc với event store, vì outbox
  không có nghĩa nếu không có transaction để bám vào.
- Nó **không** áp dụng cho đường dữ liệu từ thiết bị. Ingestion ở M2 có store-and-forward ở edge
  gateway — một cơ chế khác, giải quyết một đoạn khác của đường đi (`scope.md` §5.5).
- Nó **không** cho phép nuốt lỗi. Publish thất bại phải bị bắt, đếm, và ghi log kèm số thứ tự.

## Consequences

**Được**

- M1 không phải kéo event store từ M5 về, và không phụ thuộc vào một schema chưa ai chốt.
- Có một **con số thật** (xem *Evidence*) làm mốc cho M6 đo lại. Tiêu chí M6 đã được thêm vào
  `scope.md` §9/M6: chạy lại đúng kịch bản này với outbox, số mất phải về 0.
- Đường đi của message ở M1 đọc được trong một màn hình: dispatch → publish. Không có worker nền,
  không có bảng nào phải dọn.

**Mất / phải chịu**

- **Mất event thật.** Đo được 18 trên 200 trong một lần broker chết 30 giây. Đây không phải rủi ro
  lý thuyết.
- Cửa sổ mất **tỉ lệ thuận với thời gian broker chết**, và tỉ lệ nghịch với timeout mỗi lần publish.
  Timeout càng ngắn thì càng nhiều event rơi trong cùng một khoảng chết.
- Không phân biệt được "state đã đổi nhưng event chưa ra" với "event đã ra nhưng state chưa đổi".
  Ở M1 hậu quả nhẹ vì state đang nằm trong bộ nhớ và cũng mất khi process restart; từ M5 thì không
  còn nhẹ nữa.
- Retry in-memory của MassTransit **không cứu** được trường hợp này: nó chạy trong vòng đời của
  process, còn cái đang chết là broker.

**Việc phát sinh**

- `docs/benchmarks.md` phải có dòng ghi con số này kèm điều kiện đo. Đã có.
- M6 phải chạy lại **đúng kịch bản này** (`make bus-chaos`) sau khi có outbox, và thêm một dòng
  benchmark mới bên cạnh dòng cũ — không sửa dòng cũ.
- Cho tới khi có outbox, **không** được viết ở đâu rằng hệ thống này đạt N3.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Kéo transactional outbox từ M6 về M1 | Cần một bảng, mà bảng cần event store schema của M5. Thêm 4–6 giờ và làm M1 phụ thuộc vào một quyết định chưa chốt |
| Dùng outbox in-memory của MassTransit (`AddInMemoryOutbox`) | Nó gom publish tới cuối một consume/transaction scope — chống được "publish nửa chừng", **không** chống được process chết hay broker chết. Đúng tên gọi, sai bài toán, và cái tên sẽ làm người đọc tưởng N3 đã xong |
| Ghi event ra file rồi có worker gửi bù | Chính là outbox, chỉ đổi SQL lấy filesystem — mất transaction, thêm một cơ chế phải tự viết lại ở M6 |
| Giữ nguyên câu chữ của `scope.md` §9/M1 (*"không mất event"*) | Không đạt được ở M1, nên hoặc là lab bị bỏ qua, hoặc con số bị làm tròn về 0. Cả hai đều tệ hơn việc phát biểu lại DoD (`AGENTS.md` §1.3) |

## Evidence

`make bus-chaos` ngày 2026-08-27, máy Windows 11 / Docker Desktop, RabbitMQ 4.3.5, MassTransit 8.5.10.

Kịch bản: publish 200 event `FactoryModelRevisionActivated` cách nhau 100 ms, số thứ tự nằm ở trường
`revision`. Sau giây thứ 5, `docker compose stop rabbitmq`; chờ 30 s; `docker compose start rabbitmq`.
Mỗi lần publish có timeout 2 s.

```json
{"site":"NV1","requested":200,"published":182,"failed":18,
 "firstFailure":48,"lastFailure":65,"elapsedMs":58981}
```

```
yeu cau publish   : 200
publish thanh cong: 182
publish that bai  : 18
consumer NHAN DUOC: 182
SO EVENT MAT      : 18
```

Bốn điều đọc ra được từ bộ số này:

1. **`published` = `NHAN DUOC` = 182.** Không có event nào "publish thành công" mà consumer không
   nhận, và cũng không có event nào thất bại rồi tự đến nơi. Số mất **đúng bằng** số publish thất
   bại — không có vùng xám.
2. **Mất một khối liền, event 48 → 65.** 18 lần thất bại trong 30 giây broker chết, vì mỗi lần tốn
   hết 2 s timeout. Đây là lý do `elapsedMs` là 59 s chứ không phải 20 s như lúc chạy trơn.
3. **App không chết.** `/health/live` trả `Healthy` suốt, và `Application started` xuất hiện **đúng
   1 lần** trong log — không có restart nào. `/health/ready` chuyển `Unhealthy` **chỉ ở check
   `rabbitmq`**; năm check còn lại vẫn xanh, lỗi không lan.
4. **Broker lên lại là publish chạy tiếp, không cần restart app.** 135 event sau `lastFailure` đều
   thành công. MassTransit tự nối lại connection.

Lặp lại: `make bus-chaos`. Các tham số chỉnh qua biến môi trường `CHAOS_COUNT`, `CHAOS_DELAY_MS`,
`CHAOS_STOP_AFTER`, `CHAOS_DOWNTIME` — đổi bất kỳ cái nào thì con số đổi theo, nên phải chép cả điều
kiện đo chứ không chỉ chép "18".
