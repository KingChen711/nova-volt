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

## Message hỏng đi đâu

MassTransit tự tạo hai queue phụ cho mỗi receive endpoint:

| Queue | Chứa gì |
|---|---|
| `<queue>_error` | Message mà consumer ném exception, sau khi hết số lần thử |
| `<queue>_skipped` | Message tới đúng queue nhưng **không consumer nào nhận** kiểu đó |

`_skipped` hay bị bỏ qua và nó đáng nhìn: nó đầy lên nghĩa là có publisher đang gửi một kiểu message
mà không ai đăng ký — thường là một binding sai hoặc một consumer chưa deploy.

**Không có gì tự động dọn hai queue này.** Chúng là hàng đợi cần người xem.

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

Không cài, vì [`ADR-015`](../../../docs/adr/README.md) (`scope.md` §5.7) đã chốt sẵn hướng khác cho
nhu cầu chờ dài: **Quartz store**, ở M7 cùng Formation/Aging saga. Cài plugin bây giờ là dựng một cơ
chế mà một ADR đã quyết định không dùng.

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

**Bật ở M2**, khi load harness 5.000 msg/s đã tồn tại và có số thật để chỉnh. Ghi ngưỡng đã chọn
vào `benchmarks.md` cùng lý do.
