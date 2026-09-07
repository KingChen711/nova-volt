---
title: "M3 — Telemetry, TimescaleDB & Production Calendar"
milestone: M3
status: in_progress # Kỹ thuật có bằng chứng; teach-back ở §7 chỉ owner làm được.
created: 2026-08-30
depends_on: [M0, M1, M2]
unlocks: [M4]
---

# M3 — Telemetry, TimescaleDB & Production Calendar

M3 giữ được dữ liệu tần suất cao, đọc được nhanh và gán đúng ngày sản xuất, kể cả ở site có DST.
Đọc [AGENTS.md](../../AGENTS.md) §4, [scope.md](../scope.md) §2.3/§7.3/§8.3/§8.4/§9/M3,
[glossary.md](../glossary.md) và các ADR được dẫn dưới đây trước khi sửa.

## 1. Definition of Done

Chỉ đóng milestone khi cả năm tiêu chí, sản phẩm bắt buộc và teach-back đều hoàn tất.
Ngưỡng theo [ADR-034](../adr/ADR-034-dieu-kien-nghiem-thu-m3-sau-audit.md); không đổi để khớp số đo.

| # | Tiêu chí | Phép kiểm và bằng chứng |
|---|---|---|
| ★ D1 | Tỉ số `compressed/original` **< 15%** ở mọi phép đo. Hai cardinality cùng số ngày; hai bậc dung lượng **≥ 4× số row tại cùng cardinality**. `max − min` toàn bộ tỉ số **< 2 điểm phần trăm**; cùng điều kiện dữ liệu | `make compression-report`: 8/40 kênh × 5 ngày và 40 kênh × 7/28 ngày. Số đã đo: **8,385723 / 8,140036 / 8,147113 / 8,151416%**, spread **0,245687 pp**, bậc dung lượng **4,001399×**. Preflight đĩa/cửa sổ và owner đoán trước khi đo theo ADR-034 |
| ★ D2 | Nhiệt độ trung bình mỗi phút của `FORM-01` (**100 kênh**) trong **7 ngày**, p95 **< 200 ms**. Kiểm mức kênh và máy, cả cửa sổ riêng một máy và cửa sổ có đủ mười máy, có chunk exclusion và đọc continuous aggregate | `make rollup-bench`: một warmup, mười mẫu đan xen, `percentile_disc(0.95)`, đối chiếu từng bucket và lưu EXPLAIN của query được đo. Hai cửa sổ phải cùng bậc và cùng dưới 200 ms. Cửa sổ thứ hai có **6 ngày hoàn chỉnh**; không thay gate bảy ngày bằng cửa sổ này. Chi tiết §3 |
| ★ D3 | Ca C ở `DE1` ngày **29/03/2026** thuộc production day **28/03**, dài **7 giờ**; ngày **25/10/2026** thuộc **24/10**, dài **9 giờ**. Test phải phân biệt được implementation sai | `ProductionCalendarDaylightSavingTests`: lịch sử **8/8 xanh**, mutation **8/564 đỏ**. M3 đạt hành vi, **không đạt quy trình TDD**; owner chấp nhận riêng M3. Từ M4, RED phải tái lập trên parent SHA bằng diff chỉ chứa test và đỏ đúng assertion nghiệp vụ (ADR-034) |
| D4 | `production_day` của **05:59** và **06:01** khác nhau đúng một ngày ở **NV1 và DE1** | `ProductionCalendarTests`, hai site; `NV1` không DST, `DE1` có DST |
| D5 | Dữ liệu muộn hơn cửa sổ refresh được gộp hoặc **được đếm**, không mất im lặng | `make rollup-reconcile`: raw/parent/child **3/0/0 → 3/3/0 → 3/3/3**; hai trạng thái lệch trả `P1101`; repair parent → child trên cùng khoảng đã đóng. `make rollup-refresh-wide` báo đúng hai aggregate |

**Sản phẩm bắt buộc**

- `make ci` xanh, số test tăng so với **585** ở cuối M2; `make net-check` **9/9**,
  `make grafana-net-check` giữ Grafana khỏi `ot-net`.
- Bốn ADR đã có: [011](../adr/ADR-011-device-timestamp-la-truc-phan-manh.md),
  [012](../adr/ADR-012-production-day-va-ca-tinh-trong-gio-local.md),
  [032](../adr/ADR-032-cua-so-refresh-rollup-khong-phai-bao-dam.md),
  [033](../adr/ADR-033-minio-object-lock-cho-ban-goc-duong-cong.md). Tra tên/quan hệ tại [ADR index](../adr/README.md).
- Bằng chứng chi phí dedup toàn cục của `ingest.processed_message` trong ADR-030 và
  [benchmarks.md](../benchmarks.md); tối thiểu **10 dòng số thật** cho M3.
- Glossary có hypertable, chunk, continuous aggregate/rollup, segmentby, compression/retention
  policy, backfill, watermark, cardinality, DST, object lock, checksum.
- `event-catalog.md` không thêm domain event cho M3. `oef-mapping.md` giữ **Data Collection = đang làm**
  đến khi owner hoàn tất teach-back.

M3 không làm yield/OEE, projection engine, ánh xạ channel → cell (M7), outbox (M6), màn hình
Mendix (M4), legal hold (M12), hoặc observability/soak (M13). Rollup phục vụ đọc telemetry;
việc biến phép đo thành kết quả nghiệp vụ thuộc milestone sử dụng nó.

## 2. Thiết kế hiện hành

### 2.1 Thời gian và ngày sản xuất

`Nvm.Time` tính lịch theo timezone IANA của site, đọc từ factory model. NV1 dùng `Asia/Ho_Chi_Minh`,
DE1 dùng `Europe/Berlin`; ngày sản xuất bắt đầu **06:00 local**. Ca A 06–14, B 14–22,
C 22–06; ca C được gán về ngày bắt đầu nó. DST làm thay đổi thời gian thực của ca, không đổi
giờ chuyển ca trên đồng hồ nhà máy. Không thay phép tính này bằng trừ cứng sáu giờ trên UTC.

Giữ ba timestamp `DateTimeOffset`: `device_timestamp` là thời điểm thiết bị đo và trục phân mảnh;
`ingested_at` là lúc ingestion nhận; `recorded_at` là lúc lưu. `TimeProvider` cung cấp đồng hồ
cho code .NET (K1/K2). `clock_quality` chỉ rõ dữ liệu có lệch đồng hồ; không đổi partition key
thành thời điểm ghi để che vấn đề thiết bị.

### 2.2 Lưu và đọc telemetry

| Thành phần | Quyết định đang chạy | Vì sao cần |
|---|---|---|
| `ts.telemetry_measurement` | Hypertable trên `device_timestamp`, chunk một ngày; view `ts.process_signal` | Tách dữ liệu tần suất cao khỏi event store; giữ contract ba timestamp |
| Dedup | Claim toàn cục trong `ingest.processed_message`, FK tới telemetry | Replay sau retention không tái tạo dữ liệu đã hết hạn; không partition claim theo tháng để né tính duy nhất |
| `ts.process_signal_1m` | Rollup một phút theo site/equipment/signal, `avg/min/max/sample_count`, chỉ nhận `real`, materialized-only | Giảm lượng dữ liệu cần đọc ở mức kênh |
| `ts.process_signal_machine_1m` | Rollup tầng hai theo site/machine/signal, trung bình có trọng số | Đọc một máy không phải gộp lại tất cả row của 100 kênh; không dùng trung bình của các trung bình |
| Compression | Sau **7 ngày** cho raw và cả hai rollup | Giữ dữ liệu lịch sử với dung lượng đã đo ở D1 |
| Retention | Chính sách mong muốn raw **400 ngày**, rollup **15 năm**; **mọi job retention đều tắt tới M12** | Legal hold phải chặn mọi retention policy; rollup không có ngoại lệ |
| Refresh thường | Mỗi phút, nhìn lại **5 giờ**; parent dừng trước hiện tại **1 phút**, child **2 phút** | Child nhận parent đã được refresh; dữ liệu quá cũ cần repair/reconcile riêng |
| Refresh rộng | Lệnh vận hành thủ công, parent → child, giới hạn **399 ngày** | ADR-032 đặt **lịch tự động ở M13**; M3 cung cấp đường repair và phép đối chiếu |
| Grafana | Đã provision trong profile `obs`; đường cong formation và sample volume | `DE1` chưa có formation; panel coating chỉ có ý nghĩa khi M9 sinh dữ liệu |
| Quyền đọc | `nvm_grafana` đi qua `ts_scoped.*`, site grant ép ở server; không đọc thẳng ba bảng gốc | K3 không dựa vào biến site do dashboard/client gửi |

Dashboard dùng raw với khoảng **≤ 6 giờ**, rollup khi dài hơn. Số D2 là thời gian query server,
không gồm network/rendering và không tự chứng minh màn hình nhiều người auto-refresh đạt SLO.

### 2.3 File-drop và bản gốc

Producer ghi `<tên>.csv.partial`, **đóng file**, rồi rename nguyên tử sang `<tên>.csv.ready`.
Khi bật adapter, `PublishedSuffix` bắt buộc khác rỗng và hợp lệ; `.ready` là mặc định, có thể
cấu hình hậu tố khác như `.done`. Không còn fallback theo mtime/`SettleTime`
([ADR-036](../adr/ADR-036-file-drop-bat-buoc-publish.md), thay ngoại lệ §4 ADR-035).

Một snapshot đã claim đi xuyên parse, archive và xử lý: bản gốc giữ đúng byte của máy trong
MinIO/S3 Object Lock **COMPLIANCE 15 năm**, lưu exact version, SHA-256, actor/reason và provenance.
File vào `processed` phải có bản gốc tương ứng. Empty export vào `rejected`; lỗi archive phải retry.
Recovery trả về inbox dưới tên GUID mới, không ghi đè tên producer có thể đang dùng.
Dedup và scoped access tiếp tục áp dụng cho đường vào này.

## 3. Benchmark và vận hành

Theo [ADR-037](../adr/ADR-037-benchmark-rollup-chi-doc.md), hai lệnh đo dùng chung một runner:

```bash
make rollup-bench
make rollup-bench-line
```

Cả hai chỉ đọc dữ liệu persistent: tạo temp table trước, rồi query bằng role Grafana trong
transaction `REPEATABLE READ READ ONLY`. Không có dependency migration hoặc refresh ngầm.
Lưu stdout/stderr vào file log để giữ EXPLAIN, fingerprint, điều kiện compression và `work_mem`.

| Fixture | Cửa sổ UTC `[start, end)` | Fingerprint Temperature | Vai trò |
|---|---|---|---|
| `isolated` | 2026-07-20 → 2026-07-27, **7 ngày** | 100 kênh, **121.429** row, Good; mẫu cuối 26/07 23:59:50 | Gate mức kênh và máy `FORM-01` |
| `line` | 2026-05-13 → 2026-05-19, **6 ngày** | 1.000 kênh, **1.040.949** row, Good; mẫu cuối 18/05 23:59:55 | Cùng gate `FORM-01` trong dữ liệu mười máy; cả line là evidence |

Kiểm từng bucket giữa raw/parent/child: thiếu bucket **0**, lệch count **0**, mean trong sai số
`max(1, abs(raw_mean)) × 1e-12`. Gate đo một warmup + mười mẫu đan xen; p95 phải **< 200 ms**.
EXPLAIN của đúng query đo phải chứng minh nguồn đọc đúng và loại chunk ngoài cửa sổ, có chunk
ngoài làm đối chứng. Cả hai materialization dùng metadata công khai, không ghim số chunk **7/2**.
Không có logical chunk identity trong plan thì báo lỗi, không cho qua dựa vào số timing.

Hai cửa sổ là phép kiểm ảnh hưởng của dữ liệu khác trong database theo D2, không phải định lý về
mọi kích thước, cache hoặc tải đồng thời. `make rollup-bench-line` dùng cùng cách đo cho cả F1;
**≥ 200 ms** thì ghi nợ có hạn trước dashboard line-wide M6/M7, không đổi nó thành gate M3.

Nếu fixture đã có nhưng rollup chưa cập nhật, chuẩn bị riêng rồi mới đo:

```bash
make rollup-bench-prepare
```

Lệnh này **có ghi**: chạy migration, refresh parent → child, nén chunk chưa nén trong cửa sổ.
Không sinh lại fixture, không chuẩn hoá cache hoặc xoá mọi rowstore tail. Nó từ chối cửa sổ chưa
đóng hoặc cũ hơn **399 ngày**. Kiểm fingerprint sau chuẩn bị; sai thì sửa nguyên nhân, không sửa
expected count cho vừa số đang có.

Chỉ khi dựng database mới mới sinh fixture; các lệnh này không thuộc mỗi lượt đo:

```bash
make telemetry-backfill CHANNELS=100 DAYS=7 END_AT=2026-07-27T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0
make telemetry-backfill CHANNELS=1000 DAYS=7 END_AT=2026-05-20T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0
```

Trước backfill phải đo dung lượng trống và kiểm không đè fixture khác. Bộ line từng dừng ở ngày
thứ bảy; chỉ sáu ngày hoàn chỉnh được ghim. Ngày thứ bảy không được âm thầm thêm vào phép so cũ.
D1 còn có fixture riêng; thao tác chuẩn bị/lab và bằng chứng gốc tra [benchmarks.md](../benchmarks.md).

## 4. Kiểm chứng khi sửa

Đọc diff/contract rồi chạy targeted checks rẻ nhất. Còn finding actionable thì chưa chạy full CI.
Khi targeted sạch, `make ci` là gate cuối; lệnh nào bỏ qua phải ghi rõ cùng lý do.

| Thay đổi | Kiểm chứng phù hợp |
|---|---|
| Lịch sản xuất | Unit namespace `Nvm.UnitTests.Time`; mutation cho D3 khi sửa logic lịch |
| File-drop | `FileDropOptionsTests`, integration `FileDropTests`; kiểm producer chưa publish, archive, recovery, dedup |
| Bộ đo rollup | Hai target benchmark; đối chứng làm sai bucket/kết quả/nguồn/chunk phải đỏ, không chỉ happy path |
| SQL/migration/policy | Integration TimescaleDB và phép kiểm D1/D5 liên quan; lab có ghi cần owner đoán trước khi đo |
| Compose/network | `make net-check`, `make grafana-net-check` |
| Bản chạy thật | Kiểm image/container tạo lúc nào và cấu hình đang có; test source xanh không chứng minh container đã dùng code mới |

Lab bắt buộc của M3 vẫn là so `CAST(timestamp AS date)` với lịch sản xuất ở ca C; số đã ghi trong
ADR-012/benchmarks. Không chạy lại lab phá hoại hoặc sinh hàng triệu row chỉ vì đổi cách viết plan.

## 5. Phần kỹ thuật đã thực hiện

Ánh xạ commit phục vụ truy lại bằng chứng; chi tiết diễn biến ở git/benchmarks, không ở plan.

| Commit | Nội dung | SHA |
|---|---|---|
| C01 | `Nvm.Time`, ca và glossary | `70f0d7d` |
| C02 | Production day/shift theo timezone | `26c752b` |
| C03 | Test DST, sau implementation C02 — sai lệch quy trình đã được chấp nhận riêng M3 | `0d482ee` |
| C04 | Timezone từ factory model | `e2f6231` |
| C05 | Hypertable | `95385c1` |
| C06 | Compression và lab retention | `7dbd41c` |
| C07 | Rollup một phút | `d8cd023` |
| C08 | Backfill và chi phí dedup | `0aa5a34` |
| C09 | Compression hai cardinality | `5221785` |
| C10 | Benchmark kênh/máy | `0694812` |
| C11 | Reconcile | `0c9028a` |
| C12 | Raw curve WORM archive | `ad3abcb` |
| C13 | Grafana/scoped access | `81843a0` |
| C14 | Lab ngày sản xuất | `7474968` |
| C15 | Bằng chứng kỹ thuật, không thay teach-back | `e1fb656` |

## 6. Nợ còn mở và chỗ nhận

| # | Nợ | Hạn và điều kiện đóng |
|---|---|---|
| `N-M3-1` | Chọn `work_mem` cho dashboard theo workload đọc; mặc định 4 MB ưu tiên đường ghi | Trước dashboard line-wide **M6/M7**; đo query thực, không tăng toàn hệ thống chỉ để có số benchmark đẹp |
| `N-M3-3` | Requalification D2 ở cold-only, hot-only và cửa sổ trộn | **M13**: pre-seed cold tail trước hot head 24 giờ, kiểm raw/parent/child cùng có cold chunk nén/hot chunk rowstore và compression job tiến lên |
| `N-M3-4` | Soak producer và consumer file-drop 24 giờ | **M13**: không mất export, không đọc file chưa xong; probe ngắn không thay soak |
| `N-M3-5` | Đo lại fan-out sau khi bắt buộc publish | **M13**: số 7.558 ms lịch sử gồm 2.000 ms SettleTime, không đại diện runtime mới |
| `N-M3-6` | Bảy thư mục RabbitMQ node bỏ rơi, đã đo 5,9 MB | **M13** backup/cleanup; node active đã ghim, không tự xoá dữ liệu như một bước kiểm chứng M3 |

Kiểm cấu trúc cho materialization thứ hai đã có trong runner chung (ADR-037); không còn nợ mở
`N-M3-2`. Qualification throughput/lag ở **M9**, requalification và capacity/soak 24 giờ ở **M13**
theo ADR-031/034. Không bỏ hoặc nhập các phép kiểm khác mục đích này thành một con số.

## 7. Teach-back và điều kiện đóng

**Chưa thực hiện; vẫn chặn `status: done`.** Owner tự nói lại, không mở tài liệu. Agent không trả
lời hộ, không viết đáp án mẫu hoặc gợi ý trước (owner 2026-08-31). Tắc câu nào thì phần đó chưa xong.

1. Vì sao hypertable chia theo `device_timestamp` chứ không theo `recorded_at`; lựa chọn đó tạo rủi
   ro retention nào, và thiết kế hiện tại chặn rủi ro ấy ở đâu?
2. Instant `2026-03-29T07:00:00+02:00` ở Leipzig thuộc ca và `production_day` nào? Nếu đổi instant
   ấy sang UTC rồi trừ cứng 6 giờ thì ra ngày nào, và vì sao hai cách không tương đương?
3. Metric `compressed/original ≈ 8,14–8,39%` nói gì và không nói gì; con số khoảng 92% phải gọi
   là gì; vì sao kết quả nén đó vẫn không thay thế file CSV gốc trong WORM archive?

Sau khi owner hoàn tất và các gate kỹ thuật còn hợp lệ: đổi trạng thái plan, checklist scope và
Data Collection trong `oef-mapping.md` cùng lúc. Không suy diễn từ một CI xanh rằng milestone đã đóng.

## 8. Bàn giao M4

M4 phải kiểm `IProductionCalendar` với luồng màn hình thực, và đo workload auto-refresh trước khi
hứa latency cho UI. `ADR-023` đòi `IIdempotencyStore` bền vững **trong hoặc trước commit write đầu
tiên của M4**; không đợi M5 mới đóng K7. Đọc `scope.md` §7.5/§9/M4 và ADR-023 trước khi lập plan.
