# ADR-004 — RabbitMQ làm Manufacturing Service Bus, không dùng Kafka

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/scope.md` §4 (N15), §5.2, §9/M1 · ADR-001 (event store) · ADR-021 (MassTransit 8) · AGENTS.md K12 |

---

## Context

"Bus-Centric Design" là nguyên tắc số một của Opcenter Execution Foundation, và `scope.md` §5.2 đặt
việc dựng lại nó làm mục tiêu học chính của M1. Câu hỏi không phải *"có cần bus không"* mà
*"bus nào"*.

**Ràng buộc thật, không phải sở thích:**

| | |
|---|---|
| **N15** — ràng buộc cứng nhất | MES chết **không được** làm dừng dây chuyền. Mọi lời gọi từ tầng thiết bị lên là fire-and-forget có buffer (`scope.md` §4) |
| **K12** | Ingestion không gọi App bằng HTTP đồng bộ, chỉ qua bus |
| Throughput cần đạt | **5.000 msg/s** duy trì 10 phút, p95 lag < 5 s (N1, N2 — đo ở M2) |
| Định tuyến | Consumer phải subscribe chọn lọc theo **site** và **context**: `nvm.NV1.traceability.#` |
| Xử lý lỗi | **Retry theo từng message**, và message hỏng dừng ở một chỗ đọc được — không quay vòng, không mất |
| Replay | Đến từ **event store trên SQL Server** (ADR-001), **không** từ bus |
| Phần cứng | Một máy 15,4 GB, quota Docker 8 GB. Profile mặc định đã chiếm 6,00 GB (M0/C09.4) |

Điểm cuối cùng của bảng là điểm quyết định nhiều nhất, và nó dễ bị bỏ qua: **bus ở dự án này không
phải nguồn sự thật.** Sự thật nằm trong event store. Bus là đường vận chuyển.

Nếu không quyết bây giờ: `Nvm.Bus` không tồn tại, và toàn bộ M1 đứng.

## Decision

Dùng **RabbitMQ 4.3.5** làm Manufacturing Service Bus, qua **MassTransit 8** (ADR-021).

Topology: exchange theo bounded context, routing key `nvm.{site}.{context}.{event}.v{n}`,
queue riêng cho từng consumer, `_error` queue cho message hỏng.

Không thuộc quyết định này: MQTT ở tầng thiết bị. **EMQX vẫn giữ nguyên** — đó là giao thức của
thiết bị và của Unified Namespace, nằm trên `ot-net`/`dmz-net` và không thay thế được bằng AMQP.
Hai thứ tồn tại song song với hai vai trò khác nhau.

## Consequences

**Được**

- **Routing theo topic là bản địa.** `nvm.NV1.#` là một binding, không phải một vòng lặp lọc trong
  code. Multiplant (`scope.md` §5.6) có được gần như miễn phí.
- **Retry và dead-letter ở mức từng message.** Message thứ 4 hỏng không chặn message thứ 5. Đây là
  thứ MES cần: một cell lỗi không được làm dừng 999 cell còn lại.
- **Nhẹ.** 512 MB `mem_limit`, đo thật 126 MiB lúc rảnh (M0/C09.4). Trên máy còn 2 GB dư thì đây
  không phải chi tiết nhỏ.
- **Vận hành đơn giản**: một broker, không ZooKeeper/KRaft, không partition count phải tính trước.

**Mất / phải chịu**

- **Không có log bền để replay.** Message tiêu thụ xong là biến mất. Nếu một projection cần dựng
  lại, nó đọc **event store**, không đọc bus. Đây là ràng buộc thật, và nó là lý do M6 phải có
  transactional outbox — không có nó thì một message mất là mất hẳn.
- **Thứ tự chỉ đảm bảo trong phạm vi một queue.** Không có thứ tự toàn cục. Hai event của cùng một
  cell phải đi cùng một queue, và `partitionkey` trong envelope tồn tại vì lý do đó
  (`scope.md` §7.4).
- **Không có consumer group tự cân bằng theo partition.** Scale ngang bằng cách thêm consumer trên
  cùng queue (competing consumer), và khi làm vậy thì **mất thứ tự**. Đánh đổi này phải quyết lại
  ở từng consumer, không có câu trả lời chung.
- **Throughput có trần thấp hơn Kafka.** 5.000 msg/s nằm thoải mái trong tầm RabbitMQ, nhưng nếu
  scope tăng lên hàng trăm nghìn msg/s thì quyết định này phải mở lại.
- **Quorum queue tốn hơn classic.** RabbitMQ 4.x đã bỏ classic mirrored queue, nên bền có nghĩa là
  quorum, và quorum ghi nhiều hơn. Trên một node dev không thấy, trên cụm thật thì thấy.

**Việc phát sinh**

- Mọi receive endpoint phải `SetQuorumQueue()` (C10). Đổi loại queue sau này **không** làm tại chỗ
  được: phải xoá và tạo lại, tức là phải xử lý message đang nằm trong đó.
- Lab phá hoại C13.3 phải đo **số event mất** khi broker tắt — và con số đó là đầu vào của quyết
  định outbox ở M6 (`ADR-022`).
- Ở M13, nếu đo được RabbitMQ là nút thắt thì mở lại ADR này với số liệu, không mở bằng cảm giác.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Apache Kafka** | Xem phân tích riêng bên dưới |
| **Azure Service Bus / AWS SQS+SNS** | Cần cloud account, không chạy offline trên laptop, và làm M0 (`make up` < 5 phút, hoàn toàn local) không còn khả thi |
| **NATS / NATS JetStream** | Nhẹ và nhanh, subject routing rất hợp. Loại vì hệ sinh thái .NET và tài liệu mỏng hơn, và vì stack tham chiếu của Opcenter là AMQP — giá trị học thấp hơn |
| **Redis Streams** | Đã có Redis đâu mà dùng, và thêm một service nữa vào ngân sách 8 GB. Mô hình consumer group thô hơn, DLQ phải tự làm |
| **Chỉ dùng in-process mediator, không bus** | Vi phạm thẳng K12 và N15: Ingestion gọi App đồng bộ thì App chết là dây chuyền dừng. Đây chính là thứ M1 tồn tại để **không** làm |

### Vì sao loại Kafka — nói cho đủ

Kafka là lựa chọn nghiêm túc và lý do loại **không phải** "nặng quá". Ba lý do, xếp theo sức nặng:

1. **Mô hình xử lý lỗi không khớp.** Kafka là log có offset: consumer đọc tuần tự và commit offset.
   Một message hỏng ở giữa partition buộc phải chọn — dừng lại (chặn cả partition) hoặc bỏ qua
   (commit offset và mất nó). DLQ trong Kafka là **pattern ở tầng ứng dụng** (retry topic, DLQ
   topic), không phải cơ chế của broker. RabbitMQ ack/nack từng message, và `_error` queue là hạ
   tầng. MES cần đúng cái sau: một cell lỗi không được chặn 999 cell sau nó.

2. **Thế mạnh lớn nhất của Kafka lại là thứ dự án này không dùng.** Kafka giữ log bền và replay
   được — nhưng ADR-001 đã đặt event store ở SQL Server, và replay đọc từ đó. Trả chi phí vận hành
   của một log phân tán để rồi không replay từ nó là trả tiền cho tính năng không xài.

3. **Giá trị học thấp hơn cho mục tiêu Opcenter.** `scope.md` §5.2 nói rõ mục tiêu là dựng lại
   Manufacturing Service Bus của OEF, mà stack tham chiếu của Siemens là AMQP/bus-oriented. Dựng
   bằng Kafka thì học được Kafka, không học được thứ đang nhắm tới.

Chi phí vận hành (KRaft, partition count phải tính trước, RAM) là lý do **thứ tư** và là lý do yếu
nhất — ghi ra để nếu sau này phần cứng hết là ràng buộc, người đọc biết rằng ba lý do trên vẫn còn
nguyên giá trị.

**Khi nào mở lại quyết định này**: khi throughput cần vượt vài chục nghìn msg/s **có số đo**, hoặc
khi xuất hiện nhu cầu replay từ bus mà event store không đáp ứng được.

## Evidence

**1. RAM đo thật** (M0/C09.4, `benchmarks.md`): RabbitMQ `mem_limit` 512 MB, dùng **126 MiB** lúc
rảnh. Tổng profile mặc định 6,00 GB / 8 GB quota — không còn chỗ cho một broker nặng hơn.

**2. Ranh giới OT/IT đã dựng và kiểm** (M0/C05, C08): RabbitMQ nằm trên `it-net`, EMQX trên
`ot-net` + `dmz-net`. Đã kiểm 5 chiều: IT → OT bị chặn (`ping: bad address`), MQTT từ `it-net` bị
chặn (`Unable to connect`). Quyết định này không phá ranh giới đó.

**3. Chưa đo**: throughput thật của RabbitMQ trong cấu hình này. **Phán đoán** là 5.000 msg/s nằm
thoải mái trong tầm, dựa trên tài liệu chứ không dựa trên phép đo. Đo ở **M2** cùng N1 và ghi vào
`benchmarks.md`. Nếu sai, ADR này phải mở lại — và đó là lý do dòng này ở đây.
