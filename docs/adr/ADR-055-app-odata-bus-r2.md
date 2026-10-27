# ADR-055 — App và POM khai trong model, OData tự viết một tập con, bus bằng RabbitMQ.Client

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-28 |
| **Liên quan** | ADR-054 · ADR-021 (MassTransit 8 ở v1, nay không dùng) · ADR-053 (Next.js) · scope §3.1 |

## Context

R2 dựng phần còn lại của nền: app và extension app, POM, OData theo app, token có hạn, event lên bus với subscription
và worker role, signal rule và timer, configuration key, state machine dạng dữ liệu, cùng khung web. Hành vi phải
giống nền thật đã quan sát: app chỉ thấy thứ nó import; command của extension app chạy dưới base app với tiền tố;
event không có subscription vẫn phát mà không ai nhận; subscription có loại khai trong model và loại tạo theo plant;
reading function trả query trì hoãn; view thiếu cho HTTP 200 với thân bị cắt; token hết hạn trả 401 mã `6401`.

## Decision

1. **POM khai trong code**: lớp `AppDefinition` có `[App("…")]` import entity, facet, reading model, command của FB,
   reading function. Extension app khai `BaseApp`; extension app của extension app là lỗi model. Composite command
   khai `[AppCommand("App")]`. `SolutionConfiguration` chỉ còn command settings (pre-check bật theo app), worker role,
   plant.
2. **OData tự viết một tập con** (`Nvm.Foundation.Web`): GET entity set, đọc theo Id, reading function
   `Ten(a='x')`; `$filter` (eq ne gt ge lt le, and or not, contains, startswith, endswith), `$orderby`, `$top`, `$skip`,
   `$count`; POST command theo tên công bố. URL `/mom/{plant}/Application/{app}/odata/…`. Danh sách trả theo luồng:
   ghi đầu response trước, query chạy trong lúc ghi.
3. **Bus bằng RabbitMQ.Client** (không MassTransit): exchange topic `nvm.{plant}.events`, routing key là tên event,
   mỗi worker role một queue `nvm.{plant}.{role}`, binding theo subscription đang bật lúc worker khởi động (binding
   của subscription đã tắt bị gỡ). Event lưu vào bảng event trong transaction; publisher confirm; lỗi bus không làm
   command thất bại, relay gửi lại dòng `PublishedOn` rỗng. Worker role: số consumer, tuần tự là một consumer.
4. **Subscriber chạy trong transaction riêng** theo domain của nó, người gọi là `system`, nối vào `CorrelationId` của
   lời gọi đã phát event. Lỗi của subscriber ghi trace, rollback, message vẫn được xác nhận (không thử lại).
5. **Token**: JWT từ Keycloak, `ClockSkew = 0`; hết hạn trả `{"error":{"code":"6401"}}`, thiếu hoặc sai token trả mã
   `Unauthorized`. Role lấy từ `realm_access.roles`. Realm đặt `accessTokenLifespan` 600 giây.
6. **Signal rule và timer là dữ liệu** (`SignalRule`, `SystemTimer`); timer tới hạn chạy rule cho từng nhịp bị lỡ.
   Rule theo event nhận event qua worker role `SignalRules`; `{Tên}` trong tham số lấy từ thuộc tính của event.

## Consequences

**Được**

- Topology bus đọc được bằng mắt trên RabbitMQ UI, giống cách chẩn đoán "lệnh xong mà việc sau không chạy" (lab L04).
- Lab L05 (token hết hạn) và L15 (thiếu view → 200 thân cắt) tái hiện được bằng test.

**Mất / phải chịu**

- OData chỉ là tập con: không `$expand`, `$select`, không `$metadata` thật. Đủ cho màn và script chẩn đoán; thiếu
  thì thêm khi một mốc cần.
- Subscriber lỗi không được thử lại. Hệ thật có cơ chế thử lại hay không chưa xác minh (scope §7).
- Subscription theo plant có hiệu lực khi worker khởi động lại.
- Service layer chưa kiểm quyền theo role cho từng command; web chỉ ẩn menu và chặn route. Nợ N-R2-1.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| `Microsoft.AspNetCore.OData` với EDM động theo app | Phải dựng controller động cho từng entity set của từng app; công sức lớn hơn tập con cần dùng |
| MassTransit 8 | Tự đặt tên exchange/queue theo kiểu message, che mất topology cần luyện đọc |
| Subscriber chạy trong cùng transaction với command | Trái với quan sát: handler chạy sau commit, ngoài response |

## Evidence

- `tests/Integration/Nvm.FoundationTests`: `HttpBehaviorTests` (11), `BusBehaviorTests` (6), `ConfigurationBehaviorTests` (9).
- Chạy thật 2026-10-28: host + SQL Server + RabbitMQ + Keycloak của compose; web đăng nhập qua Keycloak, đọc và ghi
  configuration key, hiện nguyên thân lỗi `7001`.
