# ADR-043 — Context unit authoritative cho nhập kết quả đo

Status: Accepted, 2026-09-24. Trong phạm vi triển khai đã giao; giữ K1–K13.

## Vấn đề

POM trước đây và command RecordDataCollection cùng dùng fixture. Sau khi POM chuyển sang
projection từ event, một unit mới đã hiện trên màn hình nhưng command nhập tay vẫn trả
UNIT_NOT_FOUND. Nếu serial trùng fixture, nguy hiểm hơn: command có thể dùng trạng thái cũ.
Đọc lại projection PostgreSQL để kiểm command không giải quyết được độ trễ của read model.

## Quyết định

Traceability tiếp tục sở hữu ProductionUnit và replay. Hosting adapter của nó cung cấp
IUnitExecutionContextReader qua Contracts; ProductionExecution không reference implementation
của FB khác. Host Execution/Host.All cùng SQL transaction gọi query này để lấy trạng thái
authoritative, giữ reservation và stream lock tới commit của RecordDataCollection.
Đây là query nội bộ cùng process/transaction, không phải HTTP giữa các service.

SqlEventStore đọc stream bằng session đang giữ claim khi có transaction; kiểm site và dùng
UPDLOCK/HOLDLOCK trước replay. Ngoài command, reader vẫn dùng connection riêng. Nhờ vậy read
cũng thấy event vừa append trong transaction, và không tự chờ lock qua connection thứ hai.

Unit thật luôn ưu tiên hơn fixture. Fixture chỉ còn fallback cho unit PoC chưa serialize,
giữ khả năng gửi các draft cũ. POM trả riêng unit từ event, không union fixture. Khi chuyển
khỏi PoC, phải đối soát hết draft chưa gửi rồi bỏ fallback; không tự chuyển fixture thành
event lịch sử giả. RecordDataCollection vẫn chỉ ghi fact nhập tay, không hoàn tất công đoạn
hoặc quyết định chất lượng.

## Đánh đổi và kiểm chứng

Command nhập tay có lock trên unit trong thời gian transaction; command cùng unit có thể đợi.
Đổi sang các database/process độc lập cần thiết kế command boundary mới, không thay adapter
bằng remote call mà vẫn giữ lời hứa nguyên tử. HTTP integration kiểm cả NV1/DE1, trùng fixture,
Completed/Held, replay và một bản ghi/event. SQL integration dùng NOWAIT để chứng minh competing
writer bị chặn tới commit; event-store test kiểm read-own-writes và site trong transaction.
