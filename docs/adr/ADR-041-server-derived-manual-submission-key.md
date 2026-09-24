# ADR-041 — Backend suy khoá UUIDv5 cho submission nhập tay

| Field | Value |
|---|---|
| Status | Accepted, 2026-09-23 |
| Scope | M4 C07; thay yêu cầu **client phải gửi** UUIDv5 trong ADR-038, giữ nguyên natural key và K7 |

## Context

Mendix Studio Pro MCP hiện không tạo được Java action mới (`JavaActions$JavaAction cannot be created`),
và microflow không có hàm UUIDv5/SHA-1. App đã có CommunityCommons `RandomHash` trả UUIDv4 dạng D;
nó đủ tạo `submissionId` một lần khi lưu draft, nhưng không suy được UUIDv5 đúng thuật toán .NET.
Buộc client tự gửi key sẽ để C07 không thể hoàn tất qua surface hiện có. Điều quan trọng cho K7 là
backend dùng cùng natural key cho mọi lần retry, chứ không phải client tự tính được giá trị đó.

## Decision

Backend **luôn** suy `idempotencyKey = UUIDv5(siteId từ principal, "RecordDataCollection", submissionId)`
theo `IdempotencyKey.FromNaturalKey`. Request có thể bỏ `idempotencyKey` hoặc gửi chuỗi rỗng.
Nếu client gửi key, backend parse và bắt buộc khớp khoá suy ra; key sai bị từ chối trước claim.
`submissionId` phải là UUID dạng D chữ thường, được tạo một lần và giữ cùng payload/`occurredAt`
trong draft Mendix. Retry gửi nguyên submission và payload nên backend tìm lại cùng outcome; sau timeout
client không cần biết key để tránh nhân bản effect. Response và event vẫn dùng key backend suy ra.

Draft phải commit xong trong một request Mendix trước request POST. Mất response, lỗi broker hoặc
restart không cấp submission mới. Site và actor vẫn lấy từ principal; cùng key với actor/payload khác
vẫn trả conflict. Quyết định này chỉ đổi wire contract của `RecordDataCollection`, không đổi ADR-010,
thuật toán UUIDv5, event ID, transaction hoặc các command khác.

## Evidence and consequences

Unit test đối chiếu key client omitted/rỗng với vector UUIDv5; HTTP integration test gửi request không
có trường key, restart app rồi gửi lại và kiểm một outcome/event. Mendix vẫn phải qua lab C09 để chứng
minh draft bền và retry từ UI thật. Client .NET cũ gửi key hợp lệ tiếp tục chạy.

Trade-off: một client muốn correlate trước response phải tự tính UUIDv5 hoặc giữ `submissionId` làm
correlation tạm; Mendix dùng chính draft/submission để hiển thị trạng thái chưa xác nhận.
