# Architecture Decision Records

Mỗi file ghi **một** quyết định kiến trúc: bối cảnh lúc quyết, quyết định gì, phải chịu những gì,
và đã loại phương án nào vì lý do gì.

Lý do tồn tại rất cụ thể: tới tháng thứ tư bạn sẽ không nhớ vì sao chọn SQL Server, và sẽ mất một
buổi để suy luận lại — hoặc tệ hơn, sẽ đổi nó vì quên mất ràng buộc đã dẫn tới lựa chọn đó.

## Cách dùng

- Bắt đầu từ [`_template.md`](_template.md).
- Đặt tên file `ADR-NNN-<slug>.md`, số **không tái sử dụng** kể cả khi ADR bị bỏ.
- Thêm ADR mới thì **cập nhật bảng dưới đây trong cùng commit**. Bảng lệch là bảng vô dụng.
- ADR **không sửa nội dung** sau khi `Accepted`. Đổi ý thì viết ADR mới và đánh dấu cái cũ
  `Superseded by ADR-NNN`. ADR là ảnh chụp thời điểm; sửa nó là xoá mất thứ đáng giá nhất — cái
  bạn đã tin lúc đó.
- Bằng chứng phải **tái lập được**: số đo, output lệnh, trích code. Không dùng ảnh chụp màn hình
  (`docs/plans/M0-bootstrap.md` §C13).

### Khi nào viết ADR

Khi câu trả lời cho *"vì sao không làm cách kia?"* dài hơn một câu, **và** quyết định đó khó đảo
ngược sau này. Chọn tên biến không cần ADR. Chọn store thì cần.

Nếu một quyết định được nhắc tới trong `scope.md` hoặc plan bằng cụm *"ghi ADR-NNN"*, đó là
nghĩa vụ chứ không phải gợi ý.

## Đánh số

`scope.md` §5.7 đã **giữ chỗ trước** ADR-001 … ADR-018 cho các quyết định lớn xuyên suốt 14
milestone. ADR-019 trở đi cấp phát theo thứ tự phát sinh. Đừng lấy số trong khoảng đã giữ chỗ cho
việc khác.

## Index

| # | Quyết định | Trạng thái | Ra ở |
|---|---|---|---|
| [001](ADR-001-sql-server-event-store.md) | SQL Server cho event store, write model, outbox | **Accepted** 2026-08-26 | M0 · C14 |
| [002](ADR-002-postgresql-timescaledb-read-model.md) | PostgreSQL + TimescaleDB cho telemetry và read model | **Accepted** 2026-08-26 | M0 · C14 |
| 003 | Hand-rolled event store thay vì Marten | Chưa viết | M5 |
| [004](ADR-004-rabbitmq-not-kafka.md) | RabbitMQ làm Manufacturing Service Bus, không dùng Kafka | **Accepted** 2026-08-26 | M1 · C09 |
| 005 | Genealogy là DAG có thời gian | Chưa viết | M5 |
| 006 | Closure table thay vì recursive CTE | Chưa viết | M6 |
| 007 | Định dạng serial number | Chưa viết | M4 |
| [008](ADR-008-cloudevents-envelope.md) | CloudEvents đi ở transport header, không thay envelope MassTransit | **Accepted** 2026-08-26 | M1 · C12 |
| 009 | Ngữ nghĩa EPCIS cho genealogy edge | Chưa viết | M5 |
| [010](ADR-010-idempotency-key-uuid-v5.md) | Idempotency key = UUIDv5 từ natural key | **Accepted** 2026-08-26 | M1 · C04 |
| 011 | Ba loại timestamp | Chưa viết | M3 |
| 012 | Production day & shift | Chưa viết | M3 |
| 013 | OData cho Public Object Model | Chưa viết | M4 |
| 014 | Mendix là lớp UI duy nhất | Chưa viết | M4 |
| 015 | Saga dùng Quartz store thay vì delayed exchange | Chưa viết | M7 |
| 016 | OR-Tools CP-SAT cho matching | Chưa viết | M11 |
| 017 | Hold cascade là job có checkpoint | Chưa viết | M9 |
| 018 | Package versioning theo Functional Block | Chưa viết | M12 |
| [019](ADR-019-dotnet-10-lts.md) | Dùng .NET 10 LTS thay vì .NET 9 | **Accepted** 2026-08-26 | M0 · C14 |
| [020](ADR-020-no-invariant-globalization.md) | Không bật `InvariantGlobalization` | **Accepted** 2026-08-26 | M0 · C14 |
| [021](ADR-021-masstransit-8-not-9.md) | Pin MassTransit 8, không nâng lên 9 | **Accepted** 2026-08-26 | M1 · C09 |
| [022](ADR-022-publish-truc-tiep-khong-outbox-o-m1.md) | Publish thẳng lên bus ở M1, chấp nhận mất event; outbox ở M6 | **Accepted** 2026-08-27 | M1 · C13 |
| [023](ADR-023-claim-truoc-khi-chay-handler.md) | Giành chỗ trước khi chạy handler; K7 chỉ đúng trong một process cho tới M5 | **Accepted** 2026-08-27 | M1 |
| [024](ADR-024-revision-la-tai-lieu-catalog-thay-cho-mot-snapshot.md) | Revision là tài liệu bất biến; hệ thống giữ cả catalog, không giữ một snapshot | **Accepted** 2026-08-27 | M1 |
| [025](ADR-025-immutablearray-cho-collection-lo-ra-ngoai.md) | Collection lộ ra ngoài dùng `ImmutableArray<T>`, không dùng `IReadOnlyList<T>` | **Accepted** 2026-08-27 | M1 |
| [026](ADR-026-sinh-c-sharp-tu-sparkplug-proto.md) | Sinh C# từ `sparkplug_b.proto` đã vendored, không dùng thư viện Sparkplug | **Accepted** 2026-08-28 | M2 · C01 |
| [027](ADR-027-http-protobuf-gateway-ingestion-trong-dmz.md) | HTTP POST batch protobuf cho chặng gateway → ingestion trong `dmz-net` | **Accepted** 2026-08-28 | M2 · C08 |
| [028](ADR-028-file-append-only-cho-store-and-forward.md) | File append-only tự viết cho store-and-forward của edge gateway | **Accepted** 2026-08-28 | M2 · C09 |
| [029](ADR-029-rate-limit-va-backpressure-khi-xa-buffer.md) | Rate limit khi flush, và vì sao gateway phải chậm lại thay vì thử lại nhanh hơn | **Accepted** 2026-08-28 | M2 · C10 |
| [030](ADR-030-khoa-dedup-toan-cuc-khong-partition-theo-thang.md) | Khoá dedup toàn cục, không partition theo tháng | **Accepted** 2026-08-28 | M2 · C12 |

Cột **Ra ở** là milestone dự kiến, không phải cam kết. Quyết định đến sớm hơn thì viết sớm hơn.

## Quan hệ giữa các ADR đã có

ADR-022 và ADR-023 cũng là **một cặp**, và cùng một nguyên nhân gốc: ở M1 **chưa có database nào**,
nên không có transaction để nối hai việc lại. ADR-022 là cái giá phải trả ở đường ra (ghi trạng thái
rồi publish — mất 18/200 event khi broker chết); ADR-023 là cái giá ở đường vào (giành chỗ cho một
khoá dedup mà không commit được cùng effect nó bảo vệ). Đọc riêng một cái sẽ tưởng đó là hai vấn đề
khác nhau. Chúng **không** đóng lại cùng lúc: ADR-023 đóng ở **M5**, khi event store và transaction
boundary bền vững xuất hiện; ADR-022 đóng ở **M6**, khi transactional outbox nối database với
RabbitMQ. M5 làm cho việc ghi trở nên nguyên tử; M6 mới làm cho việc gửi trở nên nguyên tử với việc
ghi đó.

ADR-001 và ADR-002 là **một cặp**, đọc riêng sẽ hiểu sai. ADR-001 chọn SQL Server vì mục tiêu học
Opcenter; chính lựa chọn đó lấy đi `numrange` và `EXCLUDE` constraint, và đó là lý do ADR-002 tồn
tại. Polyglot persistence ở dự án này **không** phải một quyết định độc lập — nó là hệ quả bắt
buộc của ADR-001.
