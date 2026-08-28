# ADR-027 — Dùng HTTP POST batch protobuf cho chặng gateway → ingestion trong `dmz-net`

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `docs/plans/M2-simulator-ingestion-idempotency.md` §2.1, §3.1, §5.C08 · `docs/scope.md` §7.1, §13.1 · ADR-026 · AGENTS.md K11, K12, K13 |

---

## Context

`Nvm.EdgeGateway` đứng ở `dmz-net`; RabbitMQ chỉ đứng ở `it-net`. Đây là ranh giới an ninh đã được
`make net-check` ép từ M0: gateway **không có route** tới RabbitMQ và không được mở một route mới chỉ
để publish tiện hơn. `Nvm.Ingestion` là deployable duy nhất có hai chân `dmz-net` + `it-net`, nên
chặng gateway → ingestion cần một giao thức riêng chạy hoàn toàn trong DMZ.

Ràng buộc thật lúc quyết:

| | |
|---|---|
| Dữ liệu đầu vào | MQTT Sparkplug B protobuf, đã decode và nối với `equipment_path` tại gateway |
| Dấu thời gian phải thêm | `gateway_timestamp` từ `TimeProvider`, không ghi đè `device_timestamp` |
| SLO phía sau | ≥ 5.000 msg/s; khi backend sống lại phải xả backlog có rate limit |
| Backpressure phải nhìn thấy | Ingestion cần trả được `429`/`503` + `Retry-After` để gateway chậm lại ở C10 |
| Ranh giới | Gateway tới được EMQX, không tới được RabbitMQ; IT không tới được EMQX |
| Khoảng trống bảo mật | M2 chưa có PKI/mTLS; K13 cấm vá bằng API key hard-code |

Không quyết giao thức ở C08 thì C09 không biết buffer byte gì, C10 không có cách biểu diễn
backpressure, và C12 không biết endpoint nào phải nhận.

## Decision

Dùng **HTTP POST batch**, body `application/x-protobuf`, cho chặng gateway → ingestion trong
`dmz-net`:

```
POST /api/ingestion/v1/sparkplug-batches
Content-Type: application/x-protobuf
```

Body dùng contract NovaVolt sở hữu ở `gateway_ingress.proto`. Gateway gửi **dữ liệu đã decode**:
`site_id`, `equipment_path`, topic, loại Sparkplug, `gateway_timestamp`, và từng reading với
`device_timestamp`. Type protobuf generated là `internal`; hai deployable chỉ thấy
`DecodedSparkplugMessage` và `byte[]`.

URL mang `/v1`; field number trong v1 là append-only. HTTP thành công mới được coi là chặng này đã
nhận. C08 gửi trực tiếp và đếm loss; C09 đặt buffer bền vững trước client; C10 đọc `429`/`503` và
`Retry-After` để điều tiết xả.

Quyết định này **không** cho gateway publish RabbitMQ, không giải quyết dual-write ingestion → bus,
và không tuyên bố chặng đã có xác thực.

## Consequences

**Được**

- Dùng `curl`/proxy trong DMZ để thấy request, status và thời gian; không cần công cụ gRPC riêng.
- Backpressure hiện thành contract vận hành (`429`/`503` + `Retry-After`) thay vì nằm ẩn trong flow
  control của thư viện.
- Body protobuf giữ kiểu value và đủ hai timestamp mà không đổi Sparkplug thành JSON cho tiện.
- Gateway không biết RabbitMQ, đúng K11/K12. Ingestion là cầu nối duy nhất sang IT.
- C09 có thể lưu đúng byte sẽ POST; C10 có thể gom nhiều message thành một batch mà không đổi schema.

**Mất / phải chịu**

- Gateway phải tự sở hữu batching, retry, jitter, rate limit và store-and-forward. HTTP không làm hộ
  bất kỳ phần nào; C09 và C10 là phần bắt buộc, không phải tối ưu hoá sau.
- C08 có một cửa sổ loss có chủ đích: POST lỗi thì message đã decode bị đếm `dropped` và mất. Chỉ
  commit C09 mới được phép xoá giới hạn đó.
- Contract protobuf thứ hai phải được version theo field number. Nó là contract của NovaVolt, nên
  không có upstream nào chịu trách nhiệm khi ta reuse một số cũ.
- Một HTTP request cho mỗi MQTT publish ở C08 không đạt N1; batch thật xuất hiện khi buffer/flusher
  của C09–C10 gom nhiều record. C08 chỉ đóng đường đi và ranh giới.
- **Chặng này chưa xác thực.** Bất kỳ process nào đứng được trong `dmz-net` hiện có thể POST dữ liệu
  giả. Production phải dùng **mTLS gateway ↔ ingestion ở M13**. Không thêm API key hard-code: nó vừa
  yếu hơn mTLS vừa vi phạm K13 nếu nằm trong compose/repo.
- Ingestion phải bảo vệ kích thước body, batch count và thời gian xử lý. Một protocol debug dễ không
  tự biến thành một endpoint an toàn.

**Việc phát sinh**

- C09 lưu append-only trước khi ACK MQTT và chỉ tiến con trỏ sau HTTP success.
- C10 hiểu `429`/`503`, tôn trọng `Retry-After`, thêm jitter và rate limit.
- C12 triển khai đúng endpoint `/api/ingestion/v1/sparkplug-batches` trên cả `dmz-net` + `it-net`.
- M13 thêm mTLS, rotation certificate và phép kiểm từ chối client không có certificate.

### Poison payload trên chặng MQTT — chính sách chốt 2026-08-29

Nằm ở ADR này chứ không phải một ADR riêng, vì đây là ADR đang giữ mọi khoản nợ của **đường vào**
gateway → ingestion, kể cả khoảng trống mTLS mà M13 phải nhặt.

Một payload không decode được là **poison**: thử lại bao nhiêu lần cũng hỏng như nhau. Với MQTT QoS 1,
không ACK nghĩa là broker gửi lại mãi và **cả persistent session bị ghim vào đúng payload đó** — dây
chuyền im lặng vì một message hỏng. Nên gateway **ACK sau khi đếm**, không nack.

| Ở M2 (đã có) | Ở M13 (nhận nợ) |
|---|---|
| Counter `rejected` và structured log có topic + exception | Counter **tách theo reason** (topic lạ, alias chưa khai, protobuf hỏng), để một sự cố phân biệt được với nhiều sự cố |
| Message bị bỏ, dây chuyền chạy tiếp | Log kèm **độ dài payload + hash**, đủ để đối chiếu mà không phải giữ nội dung |
| Không lưu raw payload | Quyết định raw quarantine/replay — kéo theo retention, secret handling và rủi ro disk-DoS |

**Không** dựng raw spool ở M2. Nó kéo theo ba thứ vừa kể, và cả ba đều là nội dung của M13
(*Hardening*) chứ không phải của một milestone đang dựng đường vào. Điều M2 phải làm là **không mất
dấu**: một message bị bỏ luôn để lại một dòng log và một counter, nên câu hỏi *"có mất gì không"* trả
lời được bằng số ngay cả khi chưa giữ được nội dung.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **gRPC streaming** | HTTP/2 flow control làm backpressure hiệu quả nhưng che đúng cơ chế M2 cần nhìn và đo; debug ca đêm cũng khó hơn một status + header |
| **Gateway publish thẳng RabbitMQ** | Muốn làm phải cho `dmz-net` route vào `it-net` hoặc kéo broker xuống DMZ; cả hai biến vùng đệm thành cửa sau, vi phạm K11/K12 |
| **Publish MQTT vòng hai lên EMQX** | Trộn message của hệ thống với `NBIRTH`/`NDEATH`/`bdSeq` của thiết bị trên cùng namespace; broker thiết bị thành hàng đợi ứng dụng và mất ranh giới trách nhiệm |
| **HTTP JSON** | Debug dễ nhưng làm mất lợi ích contract có kiểu ngay sau khi vừa giữ Sparkplug protobuf thật; value union và timestamp phải được diễn giải lại bằng convention |
| **HTTP + API key trong compose** | Không xác thực máy hai chiều, secret dễ lọt vào repo/layer, và tạo một giải pháp tạm sẽ sống tới production. M13 làm mTLS đúng chỗ |

## Evidence

Các phép dưới đây chạy lại được sau C08:

```
dotnet test --solution NovaVolt.Mes.slnx -c Release --no-build
  total: 470
  failed: 0

make net-check
  K11 nguyen ven: 9/9 phep do dat.

make edge-net-check
  Gateway network membership         dmz-net      dmz-net      dat
  Gateway -> EMQX:1883               open         open         dat
  Gateway -> RabbitMQ:5672           blocked      blocked      dat
```

`HttpGatewayBatchSinkTests` đọc lại chính body request qua `SparkplugIngressBatchCodec`: method
`POST`, URL `/api/ingestion/v1/sparkplug-batches`, media type `application/x-protobuf`, và message
sau decode bằng message trước encode. Test riêng ép non-2xx phải ném, không được tính là forwarded.

Container thực nhận dữ liệu simulator và log `decoded`; vì C12 chưa có ingestion nên cùng log có
`dropped > 0`. Đây là **bằng chứng giới hạn C08 còn thật**, không phải trạng thái chấp nhận của M2;
C09 phải đưa `dropped` do backend down về 0 bằng buffer.
