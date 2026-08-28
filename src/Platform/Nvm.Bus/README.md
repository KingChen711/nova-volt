# Nvm.Bus — Manufacturing Service Bus

Ánh xạ khái niệm **Bus-Centric Design** của Opcenter Execution Foundation (bài 1 và 6 của course).
RabbitMQ + MassTransit 8. Xem [`ADR-004`](../../../docs/adr/ADR-004-rabbitmq-not-kafka.md) và
[`ADR-021`](../../../docs/adr/ADR-021-masstransit-8-not-9.md).

## Topology

| | Quy ước | Ví dụ |
|---|---|---|
| Exchange | `nvm.{context}`, kiểu **topic** | `nvm.factory-model` |
| Routing key | `nvm.{site}.{context}.{event}.v{n}` | `nvm.NV1.factory-model.revision-activated.v1` |
| Queue | `nvm.{context}.{role}` — khai bằng `[BusEndpoint]` | `nvm.factory-model.cache-updater` |
| Binding | dựng bằng `NvmTopology`, **không nối chuỗi tay** | `nvm.NV1.#` |

Không có gì trong bảng này là configurable. Topology khác nhau giữa các môi trường là topology
không ai suy luận được.

Queue là **quorum** (`SetQuorumQueue()` áp cho mọi receive endpoint). RabbitMQ 4 đã gỡ classic
mirrored queue; trên một node dev thì hai loại chạy như nhau, nên chọn sai chỉ lộ ra khi có node
thứ hai — và lúc đó đổi loại queue nghĩa là **xoá queue** cùng những gì còn nằm trong nó.

## Thêm một consumer

```csharp
[BusEndpoint("factory-model", "cache-updater")]     // tên queue: nvm.factory-model.cache-updater
public sealed class FactoryModelCacheProbe : IConsumer<FactoryModelRevisionActivated>
```

```csharp
services.AddNvmBus(options, consumers => consumers.AddNvmConsumer<FactoryModelCacheProbe>());
```

**Luôn dùng `AddNvmConsumer`, không dùng `AddConsumer` trần.** `AddConsumer` cũng biên dịch được,
cũng khởi động được, và tạo ra một queue bind vào exchange với routing key **rỗng** — trên exchange
kiểu `topic` thì rỗng không khớp gì cả. Kết quả là một service chạy tốt, xanh trên mọi dashboard, và
không xử lý một message nào. Không có lỗi ở đâu để phát hiện ra.

`AddNvmConsumer` gắn `NvmConsumerDefinition<T>`, thứ:

1. đọc các `IConsumer<T>` của class để biết nó nhận event nào (`NvmSubscription.Of`),
2. tắt consume topology mặc định của MassTransit — nếu để bật, MassTransit tự khai báo exchange
   `nvm.factory-model` bằng **kiểu mặc định của nó**, đụng với `topic` mà publisher đã khai, và
   RabbitMQ trả `PRECONDITION_FAILED` ngay lúc khởi động,
3. bind exchange của context với pattern lấy từ `NvmTopology`, không nối chuỗi tại chỗ.

Consumer nào không nhận event nào có `[EventContract]` thì bị **từ chối lúc khởi động**.

### Hai consumer = hai queue, không phải một

Đây là điểm hay bị nhầm nhất và cũng là điều D1 của M1 kiểm:

| | Nhận được gì |
|---|---|
| 2 consumer, 2 queue (mặc định ở đây) | **cả hai** cùng nhận mỗi message — fan-out |
| 2 consumer, 1 queue | mỗi message tới **một** trong hai — competing consumer |

Cả hai đều hợp lệ, cho hai mục đích khác nhau: fan-out cho "nhiều bên cùng cần biết", competing
consumer cho "chia tải một việc". Nhầm chiều thứ hai thành thứ nhất thì audit trail mất một nửa số
dòng và không ai thấy lỗi ở đâu.

## Message hỏng đi đâu

MassTransit tự tạo hai queue phụ cho mỗi receive endpoint:

| Queue | Chứa gì |
|---|---|
| `<queue>_error` | Message mà consumer ném exception, sau khi hết số lần thử |
| `<queue>_skipped` | Message tới đúng queue nhưng **không consumer nào nhận** kiểu đó |

`_skipped` hay bị bỏ qua và nó đáng nhìn: nó đầy lên nghĩa là có publisher đang gửi một kiểu message
mà không ai đăng ký — thường là một binding sai hoặc một consumer chưa deploy.

**Không có gì tự động dọn hai queue này.** Chúng là hàng đợi cần người xem.

## Health check

`AddNvmBus` khai tường minh health check mà MassTransit tự đăng ký:

| | Giá trị | Vì sao không để mặc định |
|---|---|---|
| Tên | `bus` | Mặc định là `masstransit-bus`. Mọi probe khác đặt tên theo **thứ nó kiểm** (`sqlserver`, `postgres`, `rabbitmq`), không theo thư viện |
| Tag | đúng `ready`, **không** `live` | Bus không nối được broker là lý do ngừng nhận traffic, **không** phải lý do restart process — N15 |
| `MinimalFailureStatus` | `Unhealthy` | `Degraded` trả HTTP **200**, tức instance vẫn ở trong rotation trong khi bus không chuyển nổi message |

Ba dòng này được ép bằng `BusHealthCheckTests`, không bằng bảng này.

> [!warning] `bus` KHÔNG phải probe của broker
> Nó nói về bus **trong process này**: đã khởi động chưa, receive endpoint sẵn sàng chưa. Một host chỉ
> publish thì không có receive endpoint nào, nên sau khi bus khởi động xong nó **không phát hiện được
> broker chết** — đo ở M1/C14: `Healthy` liên tục 152 giây với broker đã tắt.
>
> Việc đó thuộc về probe `rabbitmq` riêng trong host (HTTP tới management API). Hai probe, hai loại
> hỏng, không thay thế nhau. Xem `docs/benchmarks.md` §M1.

## Retry

`NvmRetryPolicy`: **5 lần thử** (1 lần đầu + 4 lần lại), khoảng cách exponential từ 200 ms, trần
5 s, cộng jitter ±25%.

Retry chạy **trong cùng một lần delivery** — message không quay lại broker giữa các lần thử, nên
consumer bị chiếm suốt cả chuỗi. Đó là lý do khoảng cách tính bằng trăm mili-giây và tổng dưới vài
giây. Chờ hàng phút là việc của redelivery theo lịch, thứ hệ thống này **không có**.

## Hai thứ CỐ Ý không làm, và lý do

### 1. Redelivery theo lịch — không cài delayed message exchange

`UseDelayedRedelivery` của MassTransit cần plugin `rabbitmq_delayed_message_exchange`. Plugin đó là
**community plugin, không đi kèm image chính thức** `rabbitmq:*-management` — phải tải `.ez` và
`rabbitmq-plugins enable` thủ công.

Không cài, vì [`scope.md` §5.7](../../../docs/scope.md) đã nêu hướng dự kiến khác cho nhu cầu chờ
dài: **Quartz store**, ở M7 cùng Formation/Aging saga. Cài plugin bây giờ là dựng một cơ chế mà
hướng đã chọn không dùng tới. `ADR-015` chốt chính thức việc này ở M7 và **chưa được viết** — đừng
trích nó như một quyết định đã có.

**Hệ quả phải biết**: một lỗi kéo dài hơn ~3 giây sẽ đẩy message vào `_error` thay vì được thử lại
sau. Ở M1 chấp nhận được vì chưa có consumer nào phụ thuộc database. Đọc lại dòng này ở M5.

### 2. Kill switch — chưa bật

`UseKillSwitch` tạm dừng một endpoint khi tỉ lệ lỗi vượt ngưỡng, mở lại sau một lúc. Lập luận ủng
hộ nó mạnh: khi một dependency chết, kill switch giữ message **nằm trong queue** thay vì đốt hết
ngân sách retry rồi rơi vào `_error` — tức là nó bảo vệ đúng N3 (không mất message).

Vẫn chưa bật, vì kill switch **toàn là số**: ngưỡng lỗi, cửa sổ theo dõi, thời gian mở lại. M1
không có traffic nào để chỉnh ba con số đó, và chỉnh một circuit breaker trên dữ liệu bằng 0 là
đoán — trái [`AGENTS.md`](../../../AGENTS.md) §1.3. Một kill switch chỉnh sai sẽ tạm dừng một
endpoint đang hoàn toàn khoẻ.

**M2 không phải chỗ bật nó.** Load harness M2 đi theo đường raw telemetry
MQTT → Edge Gateway → HTTP → TimescaleDB; nó không tạo failure traffic cho một bus consumer, nên
dùng N1 để chỉnh kill switch là trộn hai đường dữ liệu khác nhau. Đánh giá/bật ở **M6**, khi outbox
và projection consumer tạo workload domain-event thật. Chỉ bật sau khi có số lỗi, cửa sổ và recovery
time; ghi các ngưỡng vào `benchmarks.md`.
