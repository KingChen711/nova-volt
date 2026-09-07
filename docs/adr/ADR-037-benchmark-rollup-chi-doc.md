# ADR-037 — Benchmark rollup chỉ đọc và kiểm contract trực tiếp

| | |
|---|---|
| **Status** | **Accepted** — owner yêu cầu xử lý toàn bộ findings ngày 2026-09-06 |
| **Date** | 2026-09-06 |
| **Liên quan** | ADR-034 (giữ DoD), ADR-032 (refresh parent → child), plan M3 D2 |

## Context

Hai script SQL benchmark dài tổng cộng 1.668 dòng. Phần ánh xạ EXPLAIN qua catalog riêng của
TimescaleDB ép đúng 7 raw chunk / 2 rollup chunk nhưng bỏ qua materialization thứ hai, tạo nợ
`N-M3-2` ngay trên đường đọc được nghiệm thu. Refresh, recompression và đo nằm trong cùng lệnh.
Chi phí bảo trì đó không phục vụ thêm một yêu cầu nghiệp vụ của M3.

Rollup tầng máy vẫn cần: dữ liệu parent ở mức kênh buộc truy vấn cả máy gộp nhiều row. Bỏ nó
chỉ vì muốn ít code sẽ đánh đổi một tối ưu đã đo được. Quyết định này giản lược bộ đo.

## Decision

1. Một runner cho `gate` (kênh/máy, hai cửa sổ) và `line` (evidence). Fixture khai báo một chỗ;
   fingerprint kiểm row count, danh sách kênh, clock quality, hai biên thời gian và số bucket/mẫu đã ghim ở từng mức đọc của fixture bảy ngày.
2. Tạo temp table trước, rồi đo trong `REPEATABLE READ READ ONLY` bằng role `nvm_grafana`
   qua `ts_scoped`. Phép đo không chạy migration, refresh, compression hoặc sinh lại dữ liệu.
   `make rollup-bench-prepare` là thao tác riêng có ghi: refresh parent rồi child và nén chunk
   chưa nén. Nó không chuẩn hoá cache hoặc cam kết loại bỏ mọi rowstore tail.
3. Đối chiếu raw với từng rollup **từng bucket**: không thiếu bucket, sample count bằng nhau,
   sai khác mean trong `max(1, abs(raw_mean)) × 1e-12`. Mean tầng máy/line phải có trọng số.
4. Một warmup rồi mười mẫu đan xen scenario. Query server phải tính đủ count, sample count và
   weighted mean; kiểm kết quả mỗi lượt với snapshot. `percentile_disc(0.95)` là mẫu lớn nhất
   trên mười mẫu, vẫn phải **< 200 ms** cho gate mức kênh và máy ở hai cửa sổ.
5. Lưu `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON)` của đúng query được đo. Duyệt trường
   `Plans`, nối `Schema`/`Relation Name` của node **đã thực thi** với metadata công khai
   `timescaledb_information.chunks` và `continuous_aggregates`. Cả raw, parent và child đều phải
   đọc chunk đúng nguồn/trong cửa sổ, không thực thi chunk ngoài cửa sổ, và phải có chunk ngoài
   làm đối chứng. Không ghim số chunk, node type, index name hoặc catalog nội bộ.
6. Nếu EXPLAIN tương lai không còn cung cấp identity logical chunk, gate báo lỗi rõ và giữ
   nguyên EXPLAIN cho chẩn đoán; không âm thầm bỏ phép kiểm. Compression status và `work_mem`
   được in như điều kiện đo, không được diễn giải thành bằng chứng soak hoặc hot/cold qualification.

## Consequences

Đóng `N-M3-2`: cùng một phép kiểm nguồn/chunk áp dụng cả hai materialization. Chunk layout có thể
thay đổi mà không phải viết lại oracle. Script vẫn phụ thuộc cấu trúc EXPLAIN công khai và phải
được kiểm lại khi nâng engine; nó không phải một bộ phân tích plan tổng quát.

Giữ ngưỡng, hai mức đọc và hai cửa sổ của D2. Cửa sổ riêng `FORM-01` đủ **bảy ngày**; cửa sổ có
mười máy đủ **sáu ngày**, không giả vờ thành bảy. Line 1.000 kênh là evidence theo ADR-034;
nếu ≥ 200 ms thì ghi nợ trước dashboard M6/M7. Số đo chỉ kết luận cho fixture và điều kiện đã đo,
không chứng minh tốc độ bất biến với mọi database, cache hoặc tải đồng thời.

`N-M3-3` và soak 24 giờ M13 vẫn giữ nguyên. Lệnh chuẩn bị sẽ từ chối fixture ngoài cửa sổ sửa
399 ngày; khi fixture cũ hết hạn, chọn ngày mới và ghim fingerprint qua một thay đổi review được.

## Verification

Kết quả gate, evidence mức line, các đối chứng làm sai kết quả/nguồn/chunk và regression được ghi
theo ngày, HEAD và điều kiện chạy trong [benchmarks.md](../benchmarks.md). Plan M3 giữ trạng thái
`in_progress` cho tới khi owner hoàn tất teach-back; kiểm kỹ thuật không thay phần đó.
