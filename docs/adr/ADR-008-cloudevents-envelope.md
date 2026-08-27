# ADR-008 — CloudEvents đi ở transport header, không thay envelope của MassTransit

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/scope.md` §7.4, §7.5 · `docs/plans/M1-factory-model-bus.md` §3.2, §C12 · ADR-004 · ADR-010 · AGENTS.md K10 |

---

## Context

`scope.md` §7.4 định nghĩa envelope CloudEvents 1.0 cho **mọi** message trên Manufacturing Service
Bus, kèm ví dụ đầy đủ: `specversion`, `id`, `type`, `source`, `subject`, `time`,
`datacontenttype`, `dataschema`, `correlationid`, `causationid`, `partitionkey`, `data`.

Nhưng MassTransit **cũng** bọc message trong envelope của riêng nó (`messageId`, `messageType`,
`message`, `headers`…), và nó dùng envelope đó để định tuyến, retry, và dựng fault. Hai envelope
cùng muốn là lớp ngoài của một message.

Ràng buộc thật lúc quyết:

- **K10 / §7.5**: Mendix **không bao giờ** đọc bus. Nó đi qua Public Object Model (OData) và
  Command API. Ở M1, **mọi** consumer trên bus đều là .NET.
- **§7.4** vẫn là contract của event store (M6) và của Digital Battery Passport (M12) — nơi hình
  dạng đầy đủ thật sự được đọc bởi bên thứ ba.
- **AMQP 0-9-1 không có binding CloudEvents chính thức.** Spec có binding cho HTTP (`ce-`), Kafka
  (`ce_`), AMQP **1.0** (`cloudEvents:`), MQTT, NATS. RabbitMQ nói AMQP 0-9-1 — không nằm trong
  danh sách.
- Message rơi vào `_error` queue theo định nghĩa là thứ code **không deserialize nổi**. Lúc đó chỉ
  còn header để đọc.

## Decision

**Giữ envelope MassTransit làm body. Ghi thuộc tính CloudEvents thành transport header.**

Sáu thuộc tính bắt buộc, suy ra được từ chính event, tiền tố **`ce_`** mượn từ Kafka binding:

| Header | Nguồn |
|---|---|
| `ce_specversion` | hằng `1.0` |
| `ce_id` | `IDomainEvent.EventId` — **bằng `IdempotencyKey` của command sinh ra nó** (ADR-010) |
| `ce_type` | `[EventContract]` + `[EventVersion]` |
| `ce_source` | `urn:novavolt:{site}:{application}`, site lấy từ payload |
| `ce_time` | `IDomainEvent.OccurredAt`, định dạng round-trip |
| `ce_datacontenttype` | `application/json` |

Các thuộc tính **tuỳ chọn** — `subject`, `dataschema`, `correlationid`, `causationid`,
`partitionkey` — **bị bỏ hẳn**, không ghi rỗng. CloudEvents coi *vắng mặt* và *null* là hai phát
biểu khác nhau, và không cái nào trong số đó suy ra được từ `IDomainEvent`.

Envelope §7.4 **đầy đủ** vẫn là contract, chỉ ở chỗ khác: nó là hình dạng event store lưu (M6) và
hình dạng passport công bố (M12). Nó không phải hình dạng trên dây.

`ce_` là **quy ước nội bộ**, không phải chuẩn. Ghi ra ở đây và trong `CloudEventHeaders` để ba năm
nữa không ai đi tìm một spec nói điều này.

## Consequences

**Được**

- Giữ nguyên định tuyến theo message type, retry, và `_error` queue của MassTransit — tất cả đều
  dựa vào envelope của nó.
- **Message trong `_error` queue vẫn đọc được.** Payload không deserialize nổi, nhưng header nói
  nó tự nhận là gì, từ site nào, lúc nào. Đây là lợi ích thực tế lớn nhất của quyết định này.
- Một người cầm `rabbitmqadmin` — hoặc một cầu nối sang hệ thống khác — thấy được thông tin nghiệp
  vụ mà không cần thư viện .NET nào.
- `ce_id == IdempotencyKey` nối được hai tầng dedup (§7.2), và giờ **kiểm được trên dây**.

**Mất / phải chịu**

- **Message trên dây không phải CloudEvents thuần.** Ai đó cầm thư viện CloudEvents chuẩn sẽ không
  parse được ngay. Phải đọc ADR này trước.
- **Tiền tố `ce_` là quy ước tự đặt.** Nếu sau này chuyển sang AMQP 1.0 hoặc Kafka thì có binding
  thật, và quy ước này phải được ánh xạ lại.
- **Thông tin bị lặp.** `id` và `time` có ở cả header lẫn payload. Chúng được **suy ra** từ payload
  chứ không lưu riêng, nên không lệch được — nhưng vẫn là byte thừa trên mỗi message.
- **`ce_time` và trường `time` trong JSON của event store viết khác nhau.** Header dùng định dạng
  round-trip (`2026-08-25T03:15:42.1280000+00:00`), System.Text.Json cắt bớt số 0
  (`…42.128+00:00`). Cùng một thời điểm, cùng hợp lệ RFC 3339, hai cách viết. Ghi ra để không ai
  đọc thành mâu thuẫn.
- **Năm thuộc tính tuỳ chọn chưa có nguồn.** `correlationid` (work order) và `causationid`
  (operation run) là hai thứ điều tra sự cố cần nhất, và chúng **chưa** đi trên bus. Chúng đến khi
  có command mang theo chúng.

**Việc phát sinh**

- M6: event store lưu envelope §7.4 **đầy đủ**, không lưu header. Golden file đã có từ C03.
- Khi command mang `correlationId`/`causationId`, thêm hai header và cập nhật ADR này bằng một ADR
  mới, không sửa cái này.
- Nếu xuất hiện consumer ngoài .NET trên bus, mở lại quyết định.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Raw JSON, body là `data` thuần CloudEvents** (`UseRawJsonSerializer`) | Đúng chữ §7.4 nhất. Nhưng mất định tuyến theo message type của MassTransit, phải tự bind queue, và `_error` queue mất metadata mà MassTransit dùng để replay. Trả giá đó cho một lợi ích mà **không consumer nào ở M1 cần** (K10: Mendix không đọc bus) |
| **Chỉ giữ envelope MassTransit, không header CloudEvents** | Rẻ nhất, và mất đúng thứ đáng giá: message trong `_error` queue không nói được nó là gì. Cũng làm §7.4 thành tài liệu không ai kiểm |
| **Bọc CloudEvents envelope làm message type của MassTransit** (`Publish(new CloudEventEnvelope<T>(...))`) | Mọi consumer sẽ đăng ký `CloudEventEnvelope<T>` thay vì `T`, và định tuyến theo type biến thành định tuyến theo một type generic. Rối và không mua được gì |
| **Đợi AMQP 1.0** | RabbitMQ có plugin AMQP 1.0 nhưng MassTransit đi đường 0-9-1. Đổi cả hai để có một tiền tố header chuẩn là cái giá sai |

## Evidence

**1. Filter chạy trên đường code thật, không phải mô phỏng.** Test dùng chính
`UseNvmCloudEvents(...)` mà `AddNvmBus` gọi, trên in-memory transport của MassTransit. Xoá dòng
đăng ký publish pipe đi:

```
failed  EveryOutgoingEvent_CarriesTheMandatoryCloudEventsAttributes
failed  CloudEventId_IsTheEventIdAndThereforeTheCommandsIdempotencyKey
failed  Source_NamesThePlantTheEventCameFromNotTheProcessesDefault
```

**2. Bản test đầu tiên của tôi SAI, và nó giấu một bug thật.** Bản đầu tự ghi header trong test rồi
assert đọc lại được — tức là nó xanh kể cả khi filter bị xoá. Viết lại cho chạy qua filter thật thì
**ba test đỏ ngay**: filter đăng ký ở `ConfigureSend`, mà `Publish` đi qua **publish pipe** — hai
pipe khác nhau trong MassTransit. Mọi event của hệ thống đi bằng `Publish`, nên bản đầu sẽ **không
stamp gì cả** trên RabbitMQ, và không có lỗi nào để báo.

Sửa: đăng ký filter trên **cả hai** pipe.

> Đây là ví dụ rõ nhất trong repo cho luật *"test không bao giờ đỏ được thì không kiểm gì"*. Một test
> yếu không chỉ vô dụng — nó **che** đúng cái bug nó lẽ ra phải bắt.

**3. Chưa kiểm trên broker thật.** Header trên AMQP frame sẽ được đọc bằng
`rabbitmqadmin get messages` ở **C13**, khi đã có queue thật để message nằm lại.
