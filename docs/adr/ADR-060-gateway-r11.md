# ADR-060 — Gateway là tiến trình riêng, tin báo ERP có lịch chặn nằm trong MOM

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-31 |
| **Liên quan** | ADR-059 · scope §5 (R11) · lab L21 |

## Context

R8 có đường gửi tin ra ở Foundation nhưng chưa có gì đứng sau `NVM_GATEWAY_URL`; tin từ hệ ngoài vẫn vào bằng cách gọi thẳng command. R11 cần gateway có adapter Web API, Kafka, file, workflow gọi command, Ack, và tin báo ERP có lịch chặn.

## Decision

1. **Gateway là tiến trình riêng, chỉ reference Foundation.** Nó gọi MOM qua command API với token, như hệ ngoài thật; adapter và workflow là dữ liệu trong `deploy/gateway/gateway.json`. Luật lớp đã ép chiều này từ R0.
2. **Lịch chặn và hàng đợi nằm trong MOM, không nằm ở gateway.** Quyết định gửi hay chờ thuộc nghiệp vụ, nên chẩn đoán "tin không đi" đọc trong MOM. Giờ xét chặn là giờ miền Đông Mỹ, tách khỏi giờ máy chủ.
3. **Báo khi WO Complete là event subscriber ở worker tuần tự**, không phải signal rule: hai WO Complete cùng lúc không tranh bản ghi lần gửi; lỗi ở đây không đổi WO.
4. **Hệ ngoài giả lập dùng chung code với gateway ở mức thư viện**, test chạy cả hai thật trên cổng cục bộ, chỉ MOM là executor của test.

## Consequences

**Được**: khứ hồi kiểm được cả HTTP, Kafka, file; đổi nơi nhận không đụng MOM; môi trường không có gateway vẫn chạy, tin ra file outbox.

**Mất / phải chịu**: thêm một tiến trình phải chạy; tin bất đồng bộ gửi lỗi chỉ để lại trace và không được gửi lại; Kafka vào không gửi lại tin lỗi nghiệp vụ, hệ gửi biết qua Ack; hàng đợi báo gửi xong khi đã giao cho gateway, không phải khi ERP nhận.
