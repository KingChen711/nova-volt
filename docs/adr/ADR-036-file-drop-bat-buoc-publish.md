# ADR-036 — File-drop chỉ nhận export đã publish

| | |
|---|---|
| **Status** | **Accepted** — owner yêu cầu xử lý toàn bộ findings ngày 2026-09-06 |
| **Date** | 2026-09-06 |
| **Thay thế** | Chỉ ngoại lệ tắt publish contract ở ADR-035, Decision §4 |

## Context

`ADR-035` đã chứng minh mtime im vài giây không nói được file đã hoàn tất. Ngoại lệ
`PublishedSuffix=""` vẫn giữ đường đọc dựa vào `SettleTime` cho một exporter chưa được xác định.
Các producer trong repo và cấu hình đang chạy đều dùng `.csv.ready`.

## Decision

File-drop bật thì `PublishedSuffix` phải khác rỗng và hợp lệ. Mặc định vẫn là `.ready`;
producer ghi tên tạm, đóng file, rồi rename nguyên tử sang `<tên>.csv.ready`.

Xoá `SettleTime`, nhánh tắt contract và warning 2513. Cấu hình rỗng bị từ chối khi validate,
trước lúc nhận file. Cảnh báo 2514 cho file chờ publish và recovery bằng tên GUID vẫn giữ nguyên.
Các quyết định còn lại của ADR-033/035, gồm giữ đúng byte gốc, exact object version,
checksum, dedup và phạm vi site, không thay đổi.

## Consequences

Một producer không thể publish theo hợp đồng này cần một adapter được thiết kế và kiểm chứng
riêng trước khi tích hợp. Runtime không có công tắc quay về một phép đoán đã biết sai.
Không thay schema, dữ liệu đang lưu hoặc DoD M3.

## Verification

`FileDropOptionsTests.AnEnabledAdapter_CannotDisableThePublishContract` đỏ trên implementation
cũ vì cấu hình rỗng được chấp nhận; test đi kèm hai trường hợp hậu tố hợp lệ `.ready` và `.done`.
Sau sửa: unit **3/3**, integration `FileDropTests` **18/18** xanh, gồm không claim file chưa publish, giữ bản gốc, recovery và dedup. Image mới được kiểm startup với hậu tố rỗng trong container không có network/volume dữ liệu, rồi ingestion đang chạy được cập nhật và trở lại healthy. Số đo và giới hạn kiểm chứng ở [benchmarks.md](../benchmarks.md).
