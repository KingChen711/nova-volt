# ADR-028 — Dùng file append-only tự viết cho store-and-forward của edge gateway

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `docs/plans/M2-simulator-ingestion-idempotency.md` §3.3, §5.C09 · `docs/scope.md` §9/M2 · ADR-027 |

---

## Context

Gateway phải tiếp tục nhận dữ liệu khi ingestion ngừng hoạt động, kể cả khi chính gateway mất điện
giữa một lần ghi. ACK MQTT trước khi dữ liệu xuống đĩa tạo một khoảng mà EMQX đã quên message còn
gateway chưa giữ được nó; dùng RAM chỉ dời khoảng mất mát sang lúc process chết.

Ràng buộc thật lúc quyết:

| | |
|---|---|
| Mục tiêu nghiệp vụ | Không tạo lỗ hổng trên chặng thiết bị → ingestion khi backend tắt 2 phút |
| Delivery từ EMQX | QoS 1, có thể giao lại; duplicate chấp nhận được vì ingestion phải idempotent |
| Dạng dữ liệu cần giữ | Chính protobuf batch sẽ POST theo ADR-027; mỗi record tối đa 4 MiB |
| Dung lượng mặc định | Segment 64 MiB, hard cap 2 GiB, volume Docker riêng |
| Điểm bền vững | ACK MQTT chỉ sau `fsync`; con trỏ chỉ tiến sau HTTP success và `fsync` riêng |
| Failure phải chịu | `SIGKILL`, torn final record, CRC sai, cursor journal bị cắt, hết dung lượng |

Không chốt storage ở C09 thì câu “backend chết vẫn không mất message” chỉ là suy luận từ đường đóng
sạch. Đây là thành phần duy nhất giữ bản sao giữa lúc EMQX được ACK và ingestion chưa nhận.

## Decision

Dùng queue file phân đoạn do NovaVolt sở hữu trong `Nvm.EdgeGateway`:

```text
segment-00000000000000000001.nvmq: [length:int32 LE][payload][crc32]...
cursor.nvmc:                       append-only cursor record + crc32
```

Một write pump gom tối đa 128 record hoặc 20 ms vào một lần `fsync`. MQTT publish chỉ được ACK sau
task của đúng fsync batch hoàn tất. Flusher đọc mà chưa đổi cursor; HTTP thành công rồi mới append và
`fsync` cursor. Khi mở lại, record cuối thiếu byte bị cắt về boundary gần nhất; record đủ khung nhưng
CRC sai bị bỏ, đếm và vượt qua có chủ đích.

Segment đã nằm hoàn toàn trước cursor mới được xoá. Chạm hard cap thì gateway ngắt phiên nhận, giữ
nguyên dữ liệu cũ và log `Critical`; nó không ghi đè record cũ nhất. Khi flusher giải phóng dung lượng,
gateway mới nối lại persistent MQTT session. Lỗi I/O không xác định không tự coi là “đã hết đầy” — cần
operator restart sau khi xử lý storage.

Quyết định này chỉ bảo vệ queue của gateway. Nó không làm ingestion idempotent, không bảo vệ
ingestion → RabbitMQ, và chưa quyết chính sách tốc độ/retry khi xả; C10 và C12 đóng các phần đó.

## Consequences

**Được**

- Format đủ nhỏ để xem bằng `xxd`, tái hiện từng boundary và biết byte nào đã được giữ.
- Append data trước rồi append cursor tạo failure mode ưu tiên duplicate hơn loss.
- Không cần database engine thứ hai trong container edge chỉ cho một producer và một consumer.
- CRC tách “record có đủ byte” khỏi “record có đúng nội dung”; recovery không im lặng trả byte sai.
- Hard cap chặn RAM/đĩa tăng vô hạn, còn persistent MQTT session giữ publish chưa ACK ở EMQX.

**Mất / phải chịu**

- NovaVolt sở hữu toàn bộ crash-consistency: framing, rotation, cursor recovery, truncation, `fsync`
  và các thứ tự ghi. Một thay đổi tưởng nhỏ ở bất kỳ bước nào có thể tạo loss chỉ khi mất điện.
- Chỉ có **một cursor** và đọc tuần tự. Query theo timestamp/equipment hoặc hai consumer độc lập sẽ
  biến format đơn giản này thành database viết dở.
- ACK theo `fsync` làm throughput phụ thuộc storage latency. Batching giảm số syscall nhưng thêm tối
  đa 20 ms trước ACK khi tải thấp.
- Cursor journal tiếp tục tăng trong C09. Nó nhỏ hơn data nhiều nhưng vẫn cần compaction có protocol
  crash-safe nếu số lần ACK trở thành vấn đề đo được.
- CRC record hỏng chỉ cho biết phải bỏ record; nó không thể tái tạo dữ liệu. Metric và log phải làm
  corruption thành sự cố thấy được.
- Hard cap chọn **ngừng nhận dữ liệu mới**. Dây chuyền có thể tiếp tục chạy và thiết bị có thể hỏi lại,
  nhưng khoảng offline của gateway phải được vận hành xử lý; đây không phải cơ chế lưu vô hạn.
- File và cursor là contract nội bộ chưa có migration framework. Đổi framing phải hỗ trợ đọc format cũ
  hoặc drain sạch buffer trước deploy.
- Test unit qua đường đóng sạch không đủ. `make buffer-crash` 200 vòng là một phần của CI, làm thời
  gian CI dài hơn nhưng là giá phải trả trực tiếp cho quyết định tự viết.

**Việc phát sinh**

- C10 phải tôn trọng backpressure và giới hạn tốc độ xả, nếu không queue cứu dữ liệu nhưng làm ingestion
  chết lần nữa khi sống lại.
- C12 phải dedup vì crash giữa HTTP success và cursor `fsync` sẽ gửi lại batch — đây là duplicate đúng
  thiết kế, không phải bug của buffer.
- Dashboard M13 phải hiển thị `gateway.buffer.depth`, `gateway.buffer.bytes`, corrupt record, truncated
  tail và sự kiện hard-cap.
- **Thay bằng embedded store** khi xảy ra bất kỳ điều kiện nào sau:
  1. Cần query hoặc đọc lại theo predicate thay vì FIFO tuần tự.
  2. Cần nhiều consumer có cursor độc lập.
  3. `make buffer-crash` bắt đầu đỏ ngẫu nhiên dù cùng một binary và cùng môi trường kiểm chứng.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **SQLite WAL** | Crash recovery và transaction đã chín; nhưng che mất đúng bài học `fsync`/torn write/CRC của learning project, trong khi C09 chỉ cần một FIFO một consumer. Đây là phương án thay thế đầu tiên khi một trong ba điều kiện trên xuất hiện |
| **LiteDB** | API .NET tiện nhưng thêm engine/document model không đem lại lợi ích cho byte FIFO; độ chín và công cụ vận hành không hơn SQLite cho trường hợp phải thay |
| **RAM + persistent MQTT session** | EMQX chỉ giữ publish chưa ACK; nếu gateway ACK trước rồi process chết thì RAM biến mất. ACK sau HTTP lại làm mất khả năng nhận khi backend chết |
| **Một file không segment** | Append đơn giản nhưng không thể thu hồi phần đã ACK mà không copy/rewrite cả file; hard cap sẽ thành compaction stop-the-world |
| **Ghi đè record cũ nhất khi đầy** | Giữ hệ thống tiếp tục nhận nhưng tạo một lỗ ở giữa hồ sơ traceability đã có một phần. Auditor phát hiện muộn hơn thời điểm dữ liệu còn có thể hỏi lại từ thiết bị |

## Evidence

Sau C09, các phép không phá hoại chạy lại được:

```text
dotnet build NovaVolt.Mes.slnx -c Release --no-restore --nologo
  0 warning, 0 error

dotnet test --solution NovaVolt.Mes.slnx -c Release --no-build
  479 / 479 passed
```

`FileStoreAndForwardBufferTests` ghi/mở lại 10.000 record theo đúng thứ tự; sửa một CRC vẫn đọc được
hai record hàng xóm và đếm một record hỏng; cắt tail về record cuối đủ; hard cap từ chối nguyên batch,
không ghi đè record cũ, rồi nhận lại được sau khi cursor giải phóng segment. Một test riêng làm cursor
trỏ quá phần data đã sửa và kiểm journal được reset trước sequence kế tiếp.

Đường container thật cũng được chạy khi service `ingestion` chưa tồn tại. Lần recreate đầu mở lại
`depth=257`, `bytes=51480`; flusher nhận `Name or service not known (ingestion:8080)` nhưng giữ nguyên
batch pending, segment tiếp tục tăng tới 154.541 byte và `restart_count=0`. Test
`StoreAndForwardFlusherTests` ép sink hỏng lần đầu rồi chứng minh thứ tự gửi là
`first, first, second`: retry batch cũ trước khi đọc qua cursor sang record mới.

`make buffer-crash`: chủ repo dự đoán **20/200** vòng đỏ; kết quả **0/200**. Command chạy writer và
verifier trong hai process/container khác nhau, mọi writer đi qua một cursor khác 0 rồi bị `SIGKILL`,
không đi qua `Dispose`. Các vòng trải từ 2 tới hơn 400 record đã xác nhận; mọi digest đã `fsync` đều
được đọc lại, mọi payload đúng SHA-256 + CRC, và đường đọc production không vượt cursor. Chi tiết từng
điều kiện đo nằm cùng ngày ở `docs/benchmarks.md`.

## Evidence — chi phí đĩa thật của một record (2026-08-29)

`buffer_bytes` **không** là dung lượng dữ liệu đang chờ. Nó đếm byte của những segment **còn tồn
tại**: `_bytes` chỉ giảm khi cả một segment bị xoá, mà segment mặc định là 64 MiB và segment đuôi
thì không bao giờ bị xoá. Quan sát được: `buffer_depth = 0` đi kèm `buffer_bytes = 45.049.914`.

Chia tổng byte cho số record đang chờ vì thế cho ra một con số **lớn hơn sự thật khoảng 23 lần**,
và đó là cách một ước lượng cũ đi tới **4,32 KiB/message** rồi kết luận lab 30 phút
không khả thi.

| Phép đo | Cách đo | Kết quả |
|---|---|---|
| R4 | Hiệu số **trong** outage — không gì được acknowledge nên segment chỉ lớn thêm | **187,5 B/record** |
| R5 | Hiệu số trên 45 giây tải liên tục: +42.048.734 B cho +231.037 message | **182 B/message** |

**Hệ quả cho capacity**: 30 phút ở 5.000 msg/s = 9 triệu message ≈ **1,57 GiB**, nằm **trong** cap
`MaxBytes` 2 GiB hiện tại. Không cần nén representation, không cần nâng cap.

**Bài học giữ lại**: một counter tên là `bytes` không tự động trả lời câu *"dữ liệu đang chờ
chiếm bao nhiêu"*. Phải đọc định nghĩa của nó trước khi chia nó cho bất cứ thứ gì.
