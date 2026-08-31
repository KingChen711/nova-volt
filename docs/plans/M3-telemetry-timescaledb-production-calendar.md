---
title: "M3 — Telemetry, TimescaleDB & Production Calendar"
milestone: M3
duration: "1,6–2,0 tuần (19 giờ 30 phút ước lượng). `scope.md` dự trù 1 tuần — chênh lệch giải thích ở §4"
status: planned
created: 2026-08-30
depends_on: [M0, M1, M2]
unlocks: [M4]
---

# M3 — Telemetry, TimescaleDB & Production Calendar

> **Mục tiêu**: dữ liệu tần suất cao nằm đúng chỗ và **gọi đúng tên thời gian**. M2 chứng minh cái vào bằng cái ra; M3 chứng minh cái đã vào **giữ được lâu, đọc được nhanh, và thuộc đúng ngày sản xuất** — kể cả ở nhà máy có DST.
>
> Đọc trước: [`AGENTS.md`](../../AGENTS.md) §4 (K1, K2, K3, K4, K13) · [`scope.md`](../scope.md) §2.3 (ca kíp và `production_day`), §4 (N-list), §7.3 (ba loại timestamp), §8.3 (DDL TimescaleDB), §8.4 (migration & retention), §9/M3 · [`M2-simulator-ingestion-idempotency.md`](M2-simulator-ingestion-idempotency.md) §8 (ba thứ M2 để lại) · [`glossary.md`](../glossary.md) §9

---

## 1. Definition of Done

Milestone chỉ được đóng khi **cả 5** mệnh đề đúng, có bằng chứng chạy được:

| # | Tiêu chí | Cách chứng minh |
|---|---|---|
| ★ D1 | Telemetry nén xuống **< 15%** dung lượng gốc | `make compression-report`: `hypertable_detailed_size` trước và sau `compress_chunk`, đo ở **hai** cardinality khác nhau; hai tỉ số lệch **< 2 điểm phần trăm** thì mới được ngoại suy. Phát biểu lại so với `scope.md` — lý do ở §2.4 |
| ★ D2 | *"Nhiệt độ trung bình mỗi phút của `FORM-01` trong 7 ngày qua"* trả về **< 200 ms** | `make rollup-bench`: 10 lần chạy, lấy p95, kèm `EXPLAIN (ANALYZE, BUFFERS)` chứng minh **có** chunk exclusion và **có** đọc continuous aggregate. Đo ở **hai** mức: một kênh, và cả máy (mọi kênh của `FORM-01`) — §3.2 |
| ★ D3 | **Test DST**: ca C ngày **29/03/2026** và **25/10/2026** ở site `DE1` cho `production_day` **đúng**; ca C dài **9 giờ** một lần và **7 giờ** một lần. Cả hai test **đỏ trước, xanh sau** | `ProductionCalendarDaylightSavingTests`. `scope.md` §2.3 gọi đây là bài tập bắt buộc của M3 |
| D4 | `production_day` của **05:59** và **06:01** khác nhau **đúng một ngày** — ở **cả hai** site | Test chạy cho `NV1` (UTC+7, không DST) và `DE1` (có DST). Một site đúng không chứng minh gì cho site kia |
| D5 | Dữ liệu đến muộn hơn cửa sổ refresh **không biến mất im lặng**: hoặc được gộp vào rollup, hoặc **được đếm** | `make rollup-reconcile`: so `sum(sample_count)` của rollup với `count(*)` thô trên cùng một khoảng **đã đóng**. Lệch phải bằng **0**, hoặc phải có counter nói **đúng** số lệch. Lý do mệnh đề này tồn tại: §2.2 |

**Sản phẩm phụ bắt buộc**

- `make ci` vẫn xanh, số test tăng thật (M2 kết thúc ở **585** — `scope.md` Phụ lục A).
- **4** ADR: `ADR-011` (ba loại timestamp — cái nào là trục phân mảnh), `ADR-012` (production day & shift), `ADR-032` (dữ liệu đến muộn và cửa sổ refresh), `ADR-033` (bản gốc đường cong trên MinIO). `ADR-011` và `ADR-012` đã được `scope.md` §5.7 **giữ chỗ** cho M3 — đây là nghĩa vụ, không phải gợi ý.
- `ADR-030` nhận thêm §Evidence: **số đo suy giảm tốc độ insert của `ingest.processed_message` khi bảng lớn dần** (§2.5). Đây là lần đầu dự án có đủ dữ liệu để đo nó.
- `docs/glossary.md` bổ sung **hypertable**, **chunk**, **continuous aggregate / rollup**, **segmentby**, **compression policy**, **retention policy**, **backfill**, **watermark / cửa sổ refresh**, **cardinality**, **DST**, **object lock**, **checksum** — làm **trước** khi code, đúng `AGENTS.md` §5.8.2.
- `docs/benchmarks.md` mục `## M3`, tối thiểu **10** dòng số thật.
- `docs/oef-mapping.md`: dòng **Data Collection** chuyển `đang làm` → `xong` (nó đang ghi *"Chưa `xong` vì hypertable, compression và retention là M3"*).
- `docs/event-catalog.md`: **không thêm dòng nào**. M3 không phát event — xem callout dưới.
- `make net-check` vẫn **9/9** sau khi thêm Grafana, và Grafana **không** tới được `ot-net`.

**Không** thuộc M3: yield, OEE, projection engine, `ProductionUnit`, ánh xạ channel → cell (M7), outbox (M6), Prometheus/OpenTelemetry (M13), màn hình Mendix (M4), legal hold (M12).

> [!important] Ranh giới dễ trôi nhất của M3
> M3 dễ phình về **hai** phía cùng lúc: về M6 (bắt đầu tính yield/OEE trên rollup vì "dữ liệu có sẵn rồi") và về M13 (bắt đầu dựng cả stack observability vì "đã có Grafana rồi").
>
> Câu hỏi kiểm tra: *thứ tôi đang viết có phải là **đặt dữ liệu đúng chỗ** và **gọi đúng tên thời gian** không?* Nếu nó trả lời một câu hỏi **nghiệp vụ** — bao nhiêu cell đạt, máy nào dừng lâu nhất — thì nó thuộc milestone sau. Rollup 1 phút là **hạ tầng đọc**, không phải câu trả lời.
>
> Hệ quả cụ thể: M3 **không** thêm event nào vào `event-catalog.md`. Nếu bạn thấy mình định phát `SignalRollupComputed` thì bạn đang viết M6.

---

## 2. Phát hiện trước khi bắt đầu — sáu điều chỉnh so với `scope.md`

Cả sáu đã kiểm bằng file thật trong repo hoặc bằng số đã đo ở M2, không phải suy đoán.

### 2.1 Bảng telemetry thật **không** phải `ts.process_signal`, và primary key hiện tại **chặn** `create_hypertable`

| | |
|---|---|
| **Phát hiện** | [`scope.md`](../scope.md) §8.3 viết DDL cho một bảng tên `ts.process_signal`, có `value DOUBLE PRECISION NOT NULL` và `quality SMALLINT` (*OPC UA quality code*). Bảng thật M2 tạo ra tên là **`ts.telemetry_measurement`**, có **5** `value_kind` (`real`/`integer`/`boolean`/`text`/`absent`), **ba** timestamp, `clock_quality`, và **không** có cột quality nào của OPC UA. |
| **Bằng chứng** | [`src/Workers/Nvm.Ingestion/Migrations/Up/001_ingestion_dedup.sql`](../../src/Workers/Nvm.Ingestion/Migrations/Up/001_ingestion_dedup.sql) — kể cả comment: *"M3 owns conversion to a hypertable, compression and numeric process-signal projections; none of those belongs here."* M2 đã biết chỗ nối này và cố ý để lại. |
| **Vấn đề thứ hai, nặng hơn** | Bảng đó có `source_event_id UUID PRIMARY KEY`. TimescaleDB đòi **mọi unique index phải chứa cột phân mảnh**. Nên `create_hypertable` trên bảng này sẽ **bị từ chối** cho tới khi PK được phát biểu lại. |
| **Xử lý** | Giữ `ts.telemetry_measurement` làm bảng thô (nó chở được mọi `MetricValue` — bỏ đi là mất `boolean`/`text`); PK đổi thành `(source_event_id, device_timestamp)`; tạo **view** `ts.process_signal` đúng hình dạng số mà `scope.md` §8.3 hứa. Chốt ở §3.1 và §3.2, ghi `ADR-011`. |

> [!warning] Đổi PK **không** làm yếu dedup — nhưng phải nói đúng vì sao
> Thẩm quyền dedup của tầng thiết bị là `ingest.processed_message` (`ADR-030`), không phải PK của bảng telemetry: đường ghi ở [`PostgresMeasurementIngestor.cs`](../../src/Workers/Nvm.Ingestion/Persistence/PostgresMeasurementIngestor.cs) `ON CONFLICT` trên **bảng claim**, rồi mới `INSERT` các row đã giành được. PK của telemetry là **lớp phòng thủ thứ hai**, và sau M3 nó vẫn là lớp phòng thủ thứ hai — chỉ hẹp hơn một chút. Khoá ngoại giữ nguyên mệnh đề mạnh nhất: **không có row telemetry nào không có claim**.
>
> Cột **`quality SMALLINT`** của `scope.md` §8.3 thì **không** được dựng. Nó viết cho một nguồn OPC UA mà dự án này chưa từng làm — dự án đi Sparkplug B. Dựng một cột trông giống nó rồi nhét `clock_quality` vào là tạo ra một trường mà **không ai biết nó nghĩa là gì** sau sáu tháng. `ADR-011` ghi một dòng: cột này không tồn tại, và lý do.

### 2.2 `start_offset => 3 hours` của continuous aggregate **mâu thuẫn với chính điều M2 đã chứng minh**

| | |
|---|---|
| **Phát hiện** | `scope.md` §8.3 đặt `add_continuous_aggregate_policy(..., start_offset => INTERVAL '3 hours')`. Cửa sổ đó quyết định **dữ liệu đến muộn bao lâu thì vẫn còn được gộp vào rollup**. |
| **Vấn đề** | `scope.md` §7.3 mô hình một lần gateway giữ **2 giờ**, còn simulator M2 cố ý lệch clock tới **2 giờ**. Hai hiệu ứng đó có thể cộng thành **4 giờ** trên trục `device_timestamp`, vượt cửa sổ 3 giờ. Ngưỡng `Drifted` **5 phút chỉ gắn nhãn**, không chặn hay giới hạn độ lệch. Gateway thật còn chỉ có trần **2 GiB**, không có trần tuổi dữ liệu, nên 4 giờ là phạm vi vận hành đã mô hình chứ không phải bảo đảm. M2 đã đo đường buffer chạy thật: outage 141 s sinh **7.717** phép đo về muộn thành một cục. |
| **Hệ quả** | Row về muộn hơn cửa sổ vẫn **nằm trong bảng thô** và tạo invalidation, nhưng policy thường xuyên không quay lại vùng đó; rollup giữ số cũ cho tới explicit wide refresh, và **không có lỗi nào được ném**. Đúng loại lỗi M2 đã gặp hai lần: *dữ liệu vào đúng hình dạng, sai ý nghĩa*, test xanh suốt. Ai đó ở M6 sẽ tính yield trên rollup và ra một con số nhỏ hơn sự thật đúng bằng phần dữ liệu về muộn. |
| **Xử lý** | Cửa sổ refresh phải **rộng hơn tổng ngân sách đến muộn**, cộng một job refresh rộng chạy thưa, cộng **một phép đối chiếu** biến "im lặng" thành "một con số". Đó là **D5**. Chốt ở §3.3, ghi `ADR-032`. |

> Đây không phải lỗi của scope. `scope.md` §8.3 viết trước khi có một gateway biết buffer trên đĩa; M2 mới là chỗ hành vi đó thành thật và **đo được**. M3 là lần đầu hai trang tài liệu ấy gặp nhau.

### 2.3 `DE1` **không có** dây chuyền formation — nên D3 phải là test thuần, không phải truy vấn

| | |
|---|---|
| **Phát hiện** | `scope.md` §9/M3 đòi *"test DST: ca C ngày 29/03 và 25/10 ở site DE1"*, và lab so sánh *"trên bộ dữ liệu ca C"*. |
| **Bằng chứng** | [`deploy/seed/factory-model.r3.json`](../../deploy/seed/factory-model.r3.json): `DE1` chỉ có `MODULE`, `PACK`, `WAREHOUSE` — **không có** `FORMATION`. Simulator mặc định `LinePath = NOVAVOLT/NV1/FORMATION/F1` ([`SimulatorOptions.cs:23`](../../src/Workers/Nvm.Simulator/SimulatorOptions.cs)). Và `R-M2-8` đã đẩy coating sang **M9**, EOL tester sang **M8**. Nói gọn: **không có và sẽ không có** telemetry của `DE1` ở M3. |
| **Xử lý** | Tách đôi cho đúng bản chất: <br>· **D3 là test thuần của `IProductionCalendar`** — không cần một row nào trong DB. Đúng bản chất: lịch sản xuất là một **hàm domain**, không phải một truy vấn. <br>· **Lab phá hoại** (`CAST(time AS date)` vs `IProductionCalendar`) chạy trên **dữ liệu thật của `NV1`**, nơi sai số đến từ ranh giới **06:00**; rồi chạy lần hai trên **một ngày ca C của `DE1` dựng bằng chính code lịch**, nơi sai số cộng thêm **giờ DST**. Con số nào từ dữ liệu thật, con số nào dựng ra — ghi cạnh nhau, đừng trộn. |

> [!important] `TimeZoneId` **đã có sẵn** trong factory model — M3 không phải sửa gì của M1
> [`FactorySite.cs`](../../src/FunctionalBlocks/FactoryModel/Entities/FactorySite.cs) đã mang `TimeZoneId` IANA, và XML doc của nó đã viết sẵn nguyên cớ: ca C của `DE1` dài 9 tiếng một lần và ngắn 7 tiếng một lần trong năm. M1 đã đặt cái móc này đúng chỗ. Việc của M3 là **treo lịch lên móc đó**. Nếu bạn thấy mình đang sửa seed JSON, dừng lại và đọc lại mục này.

### 2.4 DoD *"24 giờ ≈ 86 triệu điểm"* không sinh được bằng đường thật, và **tỉ số nén không phụ thuộc số dòng**

| | |
|---|---|
| **Phát hiện** | `scope.md` §9/M3 đòi *"24 giờ telemetry (≈ 86 triệu điểm ở tốc độ nén) nén xuống < 15%"*. |
| **Bằng chứng** | M2 đo đường thật chạy **3.933,2 msg/s** ở topology 1.000 kênh (`benchmarks.md`, D2). 86 triệu row qua đường đó là **hơn 6 giờ** chạy liên tục — mỗi lần đo lại. Và mỗi row telemetry **bắt buộc** có một row claim trong `ingest.processed_message` (khoá ngoại), nên 86 triệu điểm là **172 triệu row**, không phải 86. |
| **Vấn đề thật sự** | Tỉ số nén là tính chất của **hình dạng dữ liệu** — cardinality, `segmentby`, và mức tương quan giữa các mẫu liên tiếp — **không** phải của số dòng. Một cột `random()` nén rất tệ; một hằng số nén tốt đến mức vô nghĩa. Nghĩa là: sinh 86 triệu điểm **giả** rồi khoe tỉ số nén là khoe một con số **không nói gì về nhà máy này**. Còn nếu hình dạng đúng thì tỉ số đã ổn định từ vài triệu row — mỗi segment khi đó đã đủ hàng nghìn mẫu. |
| **Xử lý** | Phát biểu lại D1: dùng **đúng code đường cong của simulator** để sinh dữ liệu, đo tỉ số nén ở **hai** cardinality, và **chứng minh tỉ số ổn định** thay vì giả định nó. Nếu đĩa máy đo cho phép, chạy thêm một lượt ở quy mô đầy đủ và ghi thành dòng thứ ba. Chốt ở §3.4. |

> [!warning] Đo đĩa **trước**, đừng đo sau
> `benchmarks.md` luật #1 cấm ước lượng, nên plan này **không** ghi trước 86 triệu row chiếm bao nhiêu GB. C08 phải in dung lượng trống **trước** và **sau** mỗi lượt sinh. Một milestone chết vì đầy đĩa giữa chừng mất cả buổi để dọn, và số đo lấy được trước lúc chết thì không dùng được.

### 2.5 M3 là milestone đầu tiên đủ dữ liệu để **đo** cái giá của khoá dedup toàn cục

`ADR-030` chốt khoá dedup **toàn cục, không partition theo tháng** — vì đúng đắn, và lý do đó vẫn đúng. Nhưng `ingest.processed_message` là bảng **thường** (không hypertable), PK là **UUID ngẫu nhiên**, và ở tốc độ M2 đã đo thì nó lớn thêm hàng trăm triệu row mỗi ngày. Insert vào một btree ngẫu nhiên đang lớn dần là một trong những đường cong suy giảm kinh điển nhất của PostgreSQL.

Chưa milestone nào chạm tới điều đó vì chưa milestone nào có đủ dữ liệu. C08 sinh vài chục triệu row, nên **đo được gần như miễn phí**: ghi thời gian mỗi triệu row, xem đường cong có gãy không.

**M3 không sửa.** Sửa là đổi một quyết định đúng đắn để lấy tốc độ, và đó là việc của mốc nghiệm thu năng lực (**M9**, `ADR-031`). M3 chỉ **đo và ghi vào `ADR-030` §Evidence**, đúng thói quen *"đếm, đừng giả vờ"* mà `ADR-022` đã lập.

### 2.6 Grafana chưa tồn tại, và profile `obs` cố ý để trống tới M13

`docker-compose.yml` không có Grafana, không có Prometheus. [`Makefile`](../../Makefile) ghi thẳng: *"Profile `obs` con trong cho den M13. Giu target o day de duong dan da co san, nhung dung tuong no dang bat them gi."*

Hệ quả cho tấm dashboard đầu tiên:

- Nó đọc **SQL từ TimescaleDB**, không đọc Prometheus. Panel *"throughput ingestion"* mà `scope.md` §9/M3 đòi được tính bằng `sum(sample_count)` trên rollup — một con số **của dữ liệu**, không phải của một scrape chưa tồn tại.
- Panel *"nhiệt độ coating"* mà `scope.md` §9/M3 đòi **không có dữ liệu**: `R-M2-8` đã đẩy coating sang **M9**. Dashboard đầu tiên vẽ **đường cong formation** — điện áp, dòng, nhiệt độ theo kênh — vì đó là thứ M2 thật sự sinh ra, và cũng là *"dữ liệu quý nhất nhà máy"* theo chính `scope.md` §8.3.
- Grafana vào **profile `obs`**, để `make up` không nặng thêm. M13 nhặt đúng service đó rồi thêm Prometheus bên cạnh.

---

## 3. Năm quyết định đã chốt

Chốt theo hai tiêu chí, xếp theo thứ tự: **học được nghiệp vụ lẫn kỹ thuật của domain này**, và **sát production**.

| # | Câu hỏi | Chốt | Ghi ở |
|---|---|---|---|
| Q1 | Cột nào là trục phân mảnh của hypertable? | **`device_timestamp`** — thời gian nghiệp vụ, không phải thời gian nhận | `ADR-011`, C05 |
| Q2 | `ts.process_signal` của `scope.md` §8.3 là gì? | **View** trên bảng thô; rollup dựng thẳng trên hypertable | `ADR-011`, C07 |
| Q3 | Dữ liệu đến muộn hơn cửa sổ refresh thì sao? | **Cửa sổ rộng + refresh rộng chạy thưa + đối chiếu bằng số** | `ADR-032`, C07/C11 |
| Q4 | Lấy đâu ra 7 ngày và 24 giờ dữ liệu? | **Backfill bằng đúng code đường cong của simulator, ghi bằng `COPY`** | C08 |
| Q5 | Đường cong gốc lưu thế nào? | **File gốc trên MinIO + `sha256` trong DB; DB không giữ nội dung file** | `ADR-033`, C12 |

### 3.1 Q1 — Trục phân mảnh: `device_timestamp` hay `recorded_at`

Đây là quyết định mà `ADR-011` tồn tại để ghi, và là quyết định **khó đảo ngược nhất** của M3: đổi cột phân mảnh sau khi đã có 100 triệu row là viết lại cả bảng.

| Phương án | Được | Mất |
|---|---|---|
| **A. `device_timestamp` (chốt)** | Mọi câu hỏi của kỹ sư quy trình đều theo **thời gian trên máy**: *"18 giờ formation của kênh này trông thế nào"*. Chunk exclusion hoạt động đúng cho D2. Rollup 1 phút chia theo thời gian **quy trình** — thứ duy nhất có nghĩa vật lý | Đồng hồ thiết bị sai thì row rơi vào chunk sai. Sai **vài giờ** thì vô hại; sai **vài năm** thì row rơi vào chunk mà retention policy có thể xoá — mất một hồ sơ, im lặng |
| B. `recorded_at` | Tăng đơn điệu theo đồng hồ hệ thống, chunk lấp đầy tuần tự, retention và compression **thành thật** vì chúng nói về lúc hệ thống ghi nhận | Rollup 1 phút thành **vô nghĩa vật lý**: một lần xả buffer 2 giờ nhét cả 2 giờ đường cong vào **một** bucket. Truy vấn theo thời gian quy trình mất chunk exclusion — D2 gần như chắc chắn trượt |
| C. Hai trục (partition theo `recorded_at`, index theo `device_timestamp`) | Không mất gì rõ ràng | Trả tiền index trên mọi lần ghi để mua lại thứ phương án A có sẵn. Và vẫn phải chọn một trục cho rollup — tức là vẫn phải trả lời đúng câu hỏi này |

**Vì sao A phục vụ cả hai tiêu chí**: `scope.md` §7.3 xếp ba timestamp theo **mục đích**, không theo độ tin cậy — `device_timestamp` dùng cho *"phân tích quy trình, thứ tự sự kiện thật"*. Một historian chia dữ liệu theo lúc nó **được ghi nhận** là một historian trả lời sai câu hỏi mà người ta mở nó lên để hỏi.

> [!danger] Cái giá của A phải được **đo**, không được ghi là "chấp nhận rủi ro"
> Một thiết bị có đồng hồ sai 3 năm sẽ ghi vào chunk của 3 năm trước; retention policy 400 ngày **xoá chunk đó** ở lần chạy kế tiếp. Không lỗi, không cảnh báo, mất một hồ sơ pháp lý — đúng thứ K4 tồn tại để cấm.
>
> C06 phải **dựng lại đúng tình huống đó và đếm**: chèn một row có `device_timestamp` 500 ngày trước, chạy retention thủ công, xem row còn hay mất. Con số vào `ADR-011` §Evidence, và §Consequences ghi **điều kiện phải mở lại quyết định**: khi tỉ lệ row có `|device_timestamp − recorded_at|` lớn hơn một chunk vượt ngưỡng đo được, trục phân mảnh cần một **cột dẫn xuất bị chặn hai đầu** thay vì đọc thẳng đồng hồ thiết bị. **M3 không dựng cột đó** — M3 dựng cái đếm.

### 3.2 Q2 — `ts.process_signal` là view, và rollup **không** dựng được trên view

`scope.md` §8.3 viết `ts.process_signal` như một **bảng** và dựng rollup trên nó. Bảng thật là `ts.telemetry_measurement` (§2.1). Chốt:

- **`ts.process_signal`** = view: `device_timestamp AS time`, `site_id`, `equipment_id`, `signal_code`, `unit_id`, `real_value AS value`, `clock_quality`; lọc `value_kind = 'real'`. Truy vấn nào trong `scope.md` viết theo hình dạng cũ vẫn chạy được.
- **`ts.process_signal_1m`** = continuous aggregate dựng **thẳng trên hypertable** với cùng bộ lọc. TimescaleDB **không** cho dựng continuous aggregate trên view — biết trước điều này ở đây rẻ hơn phát hiện nó lúc C07 đang chạy migration.
- Cả hai đều mang `site_id` (K3). Truy vấn không có `site_id` là truy vấn xuyên site.

**Vì sao lọc `real`**: rollup tính `avg`/`min`/`max`. `Formation/CellSerial` là chuỗi, `Formation/StepIndex` là số nguyên đếm bước — trung bình của chúng là số vô nghĩa. Bảng thô giữ tất cả; rollup chỉ nhận thứ **cộng trung bình được có nghĩa**.

> [!important] D2 có **hai** cách đọc, và cả hai đều phải được đo
> *"Nhiệt độ trung bình mỗi phút của máy X"* — `FORM-01` là **máy**, nhưng thiết bị đo là **kênh** (`FORM-01-CH-0001`). Ở 1.000 kênh, 7 ngày, mức máy phải gộp **1.000 × 10.080 ≈ 10 triệu** dòng rollup; mức kênh chỉ **10.080**. Hai con số cách nhau hai bậc.
>
> C10 đo **cả hai** rồi mới kết luận. Nếu mức máy trượt 200 ms, câu trả lời đúng là **rollup tầng hai** (continuous aggregate trên continuous aggregate, gộp theo máy) — nhưng chỉ dựng nó **sau khi có số**, không dựng trước vì linh cảm.

### 3.3 Q3 — Dữ liệu đến muộn

**Chốt ba lớp, không một lớp:**

1. **Cửa sổ refresh rộng hơn phạm vi đến muộn đã mô hình.** `start_offset` = 2 giờ backlog + 2 giờ simulator clock skew + 1 giờ biên = **5 giờ**. Đây không phải upper bound: gateway có trần byte, không có trần tuổi. `ADR-032` ghi phép tính và điều kiện phải tính lại; hai lớp sau chịu trách nhiệm cho phần đuôi không bị chặn.
2. **Một lần refresh rộng chạy thưa** (`CALL refresh_continuous_aggregate` trên vài ngày, mỗi ngày một lần) để nhặt thứ về muộn hơn cả cửa sổ. Refresh mỗi phút trên bảy ngày là trả tiền liên tục cho một chuyện hiếm.
3. **Đối chiếu bằng số** — D5. So `sum(sample_count)` của rollup với `count(*)` thô trên một khoảng **đã đóng**. Đây là lớp duy nhất **phát hiện được** khi hai lớp trên sai.

| Phương án bị loại | Vì sao |
|---|---|
| Chỉ nới `start_offset` thật rộng (ví dụ 7 ngày) | Mỗi lần refresh tính lại 7 ngày bucket, mỗi phút. Trả giá liên tục cho một chuyện hiếm, và vẫn **không** phát hiện được trường hợp muộn hơn 7 ngày |
| Bỏ continuous aggregate, tính trực tiếp mỗi lần đọc | Trượt D2 |
| Tin vào real-time aggregation | Nó chỉ nối phần **mới hơn** watermark. Row về muộn nằm **dưới** watermark — đúng chỗ nó không nhìn |

> [!important] Đây là biến thể của bài học M2, ở tầng khác
> M2 học rằng *"dedup sai không báo lỗi — nó báo thành công và trả về ít dữ liệu hơn"*. Rollup bỏ sót dữ liệu về muộn là **cùng một loại lỗi**: không exception, không log, chỉ là một con số nhỏ hơn sự thật. Cách chống cũng cùng loại: **đếm hai vế và bắt chúng bằng nhau**.

### 3.4 Q4 — Lấy đâu ra dữ liệu để đo

**Chốt: một chế độ `backfill` ghi thẳng vào PostgreSQL bằng `COPY`, dùng lại `FormationChannel` của simulator và đúng code sinh khoá của ingestion.**

| Phương án | Được | Mất |
|---|---|---|
| **A. Backfill bằng `COPY`, tái dùng code đường cong (chốt)** | Hình dạng dữ liệu **giống hệt** đường thật, nên tỉ số nén nói đúng về nhà máy này. Sinh xong trong ít phút, nên đo lại được nhiều lần | Bỏ qua MQTT/gateway/HTTP. Phải ghi rõ điều đó cạnh **mọi** con số |
| B. Chạy rig thật đủ 6 giờ | Không bỏ qua chặng nào | Mỗi lần đo lại mất 6 giờ, và nó đo **đường ống** — thứ M2 đã đo rồi. M3 đo **kho lưu trữ** |
| C. `generate_series` + `random()` | Nhanh nhất | Tỉ số nén thành số hư cấu (§2.4). Đây là phương án làm hỏng cả milestone mà vẫn cho ra một bảng số trông rất đẹp |

Backfill **phải** sinh `source_event_id` bằng đúng `MeasurementNaturalKey` + `DeterministicGuid` của M1/C04, và **phải** ghi cả row claim trong `ingest.processed_message`. Hai lý do: khoá ngoại đòi thế, và một bộ dữ liệu mà dedup không đúng với nó là bộ dữ liệu mà mọi phép đối chiếu của M3 chạy trên nền cát.

### 3.5 Q5 — Bản gốc đường cong

**Chốt: file CSV gốc trên MinIO; DB giữ `object_key` + `sha256` + `byte_size`, không giữ nội dung file.**

`scope.md` §8.3 gọi đường cong formation là *"dữ liệu quý nhất nhà máy"* và đòi lưu **hai nơi**: chuỗi nén trong hypertable **và** file gốc trên MinIO. Nghe như thừa, nhưng không:

- Hypertable là **dữ liệu đã qua xử lý** — đã decode, đã gán equipment path, đã phân loại `clock_quality`.
- Auditor có quyền đòi **file mà máy xuất ra**, và câu *"chúng tôi đã chuẩn hoá nó rồi"* không phải một câu trả lời hợp lệ.
- `sha256` biến "file này là bản gốc" từ một lời khẳng định thành một **phép kiểm chạy được**.

Hai chi tiết phải quyết **ngay lúc tạo bucket**, vì sửa sau đắt hơn nhiều:

1. **Object lock bật lúc `mc mb`** — không bật được sau khi bucket đã tồn tại. `scope.md` §8.4 đòi raw curve giữ 15 năm có object lock.
2. **Client S3 phải kiểm license trước khi pin**, đúng quy trình M1/C09 đã lập sau chuyện MassTransit v9. Ghi kết quả kiểm vào `ADR-033`.

---

## 4. Tổng quan 15 commit

| # | Commit message | Giai đoạn | Ước lượng | Phụ thuộc |
|---|---|---|---|---|
| C01 | `feat(time): add nvm.time project with shift definitions` | A — Lịch sản xuất | 60' | — |
| C02 | `feat(time): compute production day and shift in the site time zone` | A | 90' | C01 |
| C03 | `test(time): pin both daylight saving days at de1` | A | 90' | C02 |
| C04 | `feat(time): resolve the site time zone from the factory model` | A | 60' | C02 |
| C05 | `feat(ingestion): turn telemetry into a hypertable on device time` | B — Hypertable | 90' | — |
| C06 | `feat(ingestion): add compression and retention policies` | B | 75' | C05 |
| C07 | `feat(ingestion): add the one minute rollup and its refresh window` | B | 90' | C05 |
| C08 | `feat(tools): backfill telemetry from the simulator curve model` | C — Dữ liệu & số đo | 120' | C05 |
| C09 | `test(ingestion): measure compression at two cardinalities` | C | 75' | C06, C08 |
| C10 | `test(ingestion): measure the seven day per minute query` | C | 60' | C07, C08 |
| C11 | `test(ingestion): count what the rollup misses when data arrives late` | C | 75' | C07, C08 |
| C12 | `feat(ingestion): archive the raw formation curve to minio with its digest` | D — Bằng chứng & đóng | 90' | C08 |
| C13 | `feat(obs): add grafana with the first formation dashboard` | D | 90' | C07, C08 |
| C14 | `test(time): compare the naive date cast against the production calendar` | D | 60' | C04, C08 |
| C15 | `docs: close m3 with benchmarks and checklist` | D | 45' | tất cả |

**Tổng: 1.170 phút = 19 giờ 30 phút.**

> [!warning] Đây **nhiều hơn** 1 tuần mà `scope.md` §9 dự trù, và nói thẳng thì tốt hơn giả vờ
> Với 10–12 giờ/tuần, 19,5 giờ là **1,6–2,0 tuần**. Nguyên nhân không phải plan phình ra, mà là M3 gói **hai chủ đề độc lập** vào một milestone: một **hàm domain** (lịch sản xuất, với hai ngày trong năm mà cả ngành hay làm sai) và một **engine lưu trữ** (hypertable, compression, retention, rollup). Cộng thêm thứ mà ước lượng gốc không đếm: **bộ dữ liệu để chứng minh cả hai** không tự có, phải sinh ra (§2.4).
>
> Cắt theo `R-M3-8` (C12 → M7, C13 → M13) đưa về **16 giờ 30 ≈ 1,4 tuần**. Phần còn lại là công việc thật, và cả ba DoD ★ đều nằm trong đó. Cắt tiếp thì không còn gọi là M3 được nữa.

Giai đoạn A làm được cả lúc Docker tắt. B, C, D **bắt buộc** `make up`.

> [!important] Nhắc lại từ AGENTS.md §1.1
> Agent **không tự commit**. Xong mỗi C, dừng lại, báo cáo, bạn tự đọc `git diff` rồi commit. Commit message ở cột trên là **đề xuất**.

> [!important] Nhắc lại từ AGENTS.md §5.8
> Mỗi commit **mở đầu** bằng khối **Nghiệp vụ**, rồi mới code. M3 có ít từ vựng mới hơn M2 nhưng **khó hơn**: `production_day` không phải một kiểu dữ liệu, `chunk` không phải một chi tiết cài đặt, và `retention` là một cam kết pháp lý. Từ nào chưa có trong [`glossary.md`](../glossary.md) thì thêm **trong chính commit đó** — hoặc tốt hơn, thêm cả cụm ở C01 trước khi viết dòng code đầu tiên.

> [!note] ADR viết tại chỗ quyết định
> `ADR-012` trong **C03** (khi hai ngày DST đã đỏ rồi xanh, không phải trước đó). `ADR-011` trong **C05**. `ADR-032` trong **C07**. `ADR-033` trong **C12**. `ADR-011` §Evidence nhận số của **C06**; `ADR-012` §Evidence nhận số của **C14**; `ADR-030` §Evidence nhận số của **C08**.

---

## 5. Chi tiết từng commit

### C01 — `feat(time): add nvm.time project with shift definitions`

**Mục tiêu**: có một chỗ để đặt khái niệm thời gian nghiệp vụ, và có đủ từ vựng trước khi viết logic.

**Việc làm**
- Project `src/Platform/Nvm.Time` (`scope.md` §5.4 đã giữ chỗ sẵn dòng này). Không reference EF Core, Npgsql, MassTransit (K9).
- `Shift` — `A` / `B` / `C` — và `ShiftDefinition` (giờ bắt đầu **local**, độ dài danh nghĩa). Bảng ca là **dữ liệu**, không phải `switch-case`: `scope.md` §2.3 cho một bảng, và M10 sẽ có site có bảng khác.
- `ProductionDay` — kiểu riêng bọc `DateOnly`, không dùng `DateOnly` trần. Lý do ở khối Nghiệp vụ dưới.
- [`docs/glossary.md`](../glossary.md) nhận cả cụm từ M3 (§1) — **trước** khi code, `AGENTS.md` §5.8.2.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: bảng ca phủ **đúng 24 giờ** không chồng lấn, không hở; `Shift.C` là ca duy nhất **vắt qua nửa đêm**; `ProductionDay` không ép ngầm sang `DateOnly` hay `DateTime`.

#### C01.1 — Nghiệp vụ: vì sao "ngày" là một kiểu riêng, không phải một `DateOnly`

Trong nhà máy, câu *"sản lượng ngày 25"* không nói về ngày dương lịch. Nó nói về **chu kỳ sản xuất bắt đầu lúc 06:00 ngày 25 và kết thúc lúc 06:00 ngày 26**. Ca C chạy từ 22:00 ngày 25 tới 06:00 ngày 26 — **6 tiếng của nó nằm ở ngày dương lịch 26**, nhưng cả ca thuộc `production_day = 25`. Người vận hành ca C ký nhận sản lượng vào ngày 25; ERP nhận sản lượng ngày 25; auditor hỏi về ngày 25.

Nếu `production_day` chỉ là một `DateOnly`, thì sớm muộn ai đó sẽ gán cho nó `DateTime.Today`, hoặc `CAST(time AS date)` — và không có gì trong hệ thống ngăn được, vì hai bên cùng kiểu. Sai lệch chỉ lộ ra ở báo cáo cuối tháng, dưới dạng *"ca C hình như thiếu sản lượng"*, và mất vài ngày để tìm.

Làm sai thì hỏng gì trên dây chuyền: sản lượng ca C bị chia đôi giữa hai ngày. Yield theo ngày sai. Và khi truy một lô hàng lỗi *"sản xuất ngày nào"*, câu trả lời trỏ sai ngày — tức là **recall trỏ sai lô**.

---

### C02 — `feat(time): compute production day and shift in the site time zone`

**Mục tiêu**: `IProductionCalendar` — hàm domain trung tâm của M3.

**Việc làm**
- `IProductionCalendar` với đúng ba phép của `scope.md` §9/M3:
  - `GetProductionDay(DateTimeOffset instant, SiteId site)`
  - `GetShift(DateTimeOffset instant, SiteId site)`
  - `GetShiftBoundaries(ProductionDay day, Shift shift, SiteId site)` → trả về **khoảng `DateTimeOffset`**, tức hai mốc **tuyệt đối**, không phải giờ local.
- Cài đặt: đổi instant sang **giờ local của site** bằng `TimeZoneInfo`, so với bảng ca **trong giờ local**, rồi mới suy ra ngày. **Không** trừ 6 giờ trên UTC — `scope.md` §2.3 gọi thẳng đó là cái bẫy.
- `GetShiftBoundaries` phải xử lý được hai giờ local **không tồn tại** (ngày lùi đồng hồ mùa xuân) và **tồn tại hai lần** (ngày tiến đồng hồ mùa thu). Quy tắc chọn phải viết thành comment và thành test, không để mặc định của thư viện quyết hộ.
- Mọi API nhận `DateTimeOffset` (K2). `TimeProvider` được tiêm, không gọi tĩnh (K1).

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: 05:59 và 06:01 giờ local cho hai `production_day` **cách nhau đúng 1 ngày** (**D4**); ranh giới ca A/B/C ở đúng 06/14/22 local; `GetShiftBoundaries` trả về khoảng **đóng-mở**, hai ca liền nhau không chồng lấn một mili-giây nào.

> [!warning] `TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")` chạy được là **nhờ một quyết định cũ**
> ID kiểu IANA chỉ tra được vì `ADR-020` đã cấm `InvariantGlobalization`. Một test phải **khẳng định** điều đó — tra ID của cả hai site và fail với thông báo rõ ràng — để nếu ai đó bật lại cờ ấy vì "giảm kích thước container", hệ thống hỏng **ngay ở test**, chứ không hỏng ở báo cáo sản lượng sáu tháng sau.

---

### C03 — `test(time): pin both daylight saving days at de1`

**Mục tiêu**: **D3** — mệnh đề quan trọng nhất của nửa domain trong M3.

**Việc làm**
- `ProductionCalendarDaylightSavingTests`, site `DE1` (`Europe/Berlin`):
  - **29/03/2026** — lùi đồng hồ về phía trước (02:00 → 03:00): ca C của `production_day = 2026-03-28` dài **7 giờ**.
  - **25/10/2026** — đẩy đồng hồ lùi lại (03:00 → 02:00): ca C của `production_day = 2026-10-24` dài **9 giờ**.
  - Một instant nằm **trong giờ lặp lại** của ngày 25/10 rơi vào **đúng một** ca và **đúng một** production day.
  - Cùng bộ test chạy lại cho `NV1` (không DST): cả hai ngày đó ca C dài **đúng 8 giờ**.
- **Đỏ trước, xanh sau** — `scope.md` §9/M3 đòi đúng chữ này. Viết test, chạy, **nhìn nó đỏ**, rồi mới sửa cài đặt. Nếu nó xanh ngay từ đầu thì hoặc test sai, hoặc bạn đã viết code cho nó từ C02 — cả hai đều phải kiểm lại.
- `ADR-012` viết **trong commit này**: định nghĩa `production_day`, vì sao tính trong giờ local chứ không trừ giờ trên UTC, và hai giờ bất thường mỗi năm được xử lý theo quy tắc nào.

**Kiểm chứng**
```bash
make test
```
Ngoài số test tăng: ghi vào `benchmarks.md` **độ dài thật của ca C** ở cả hai ngày và cả hai site — bốn con số. Đây là loại số mà sáu tháng sau không ai tự suy lại được.

#### C03.1 — Nghiệp vụ: hai ngày trong năm mà mọi hệ thống MES đều gặp

Ngày lùi đồng hồ, ca C ở Leipzig chỉ dài 7 tiếng — công nhân làm ít hơn một tiếng, máy chạy ít hơn một tiếng, và **sản lượng ca đó thấp hơn bình thường mà không ai làm gì sai**. Ngày kia thì ngược lại: 9 tiếng, và giờ từ 02:00 đến 03:00 **xảy ra hai lần**.

Hệ thống tính OEE bằng *"sản lượng / (8 giờ × công suất)"* sẽ báo ca đó tụt hiệu suất 12,5% — và sẽ có một cuộc họp về nó. Hệ thống lấy `production_day` bằng phép trừ 6 giờ trên UTC sẽ đẩy một phần ca C sang ngày hôm sau, và **hai ngày liền kề đều sai**, ngày này thừa đúng bằng ngày kia thiếu.

Đây là lý do `DE1` có mặt trong dự án này. Nó không phải một site trang trí — nó là **một test case sống**.

---

### C04 — `feat(time): resolve the site time zone from the factory model`

**Mục tiêu**: lịch lấy múi giờ từ **nguồn sự thật**, không từ một bảng tra thứ hai.

**Việc làm**
- `IProductionCalendar` đọc `TimeZoneId` qua factory model đang có hiệu lực (`IActiveFactoryModel` / `IFactoryModelCatalog` của M1), không tự giữ dictionary `SiteId → tz`.
- Site không có trong model → **ném**, không mặc định về UTC. Đây là cùng một quy tắc M2/C02 áp cho alias Sparkplug lạ: **không đoán**.
- Múi giờ được đọc từ **revision đang có hiệu lực tại site đó** (`ADR-024`: staged rollout là bình thường, `NV1` và `DE1` chạy revision khác nhau được).

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: đổi `TimeZoneId` trong seed → `production_day` của cùng một instant **đổi theo**, không cần sửa dòng code nào; site lạ → exception có nói tên site; `Nvm.Time` **không** reference `Nvm.FactoryModel` ngược chiều (kiểm bằng architecture test có sẵn).

> [!important] Một bảng tra thứ hai là một bảng tra sẽ lệch
> Cám dỗ ở đây là hard-code `{"NV1": "Asia/Ho_Chi_Minh", "DE1": "Europe/Berlin"}` cho nhanh — hai dòng, chạy ngay. Nhưng lúc M10 thêm site thứ ba, người thêm nó vào factory model sẽ **không biết** có bảng thứ hai phải sửa. Hệ thống khi đó tính giờ site mới bằng múi giờ mặc định, và không có gì đỏ.

---

### C05 — `feat(ingestion): turn telemetry into a hypertable on device time`

**Mục tiêu**: bảng thô thành hypertable, trên trục thời gian đúng.

**Việc làm**
- Migration `003_telemetry_hypertable.sql` (+ script `Down` tương ứng — `scope.md` §8.4 đòi mọi migration có đường lùi):
  - Bỏ `PRIMARY KEY (source_event_id)`, đặt lại `PRIMARY KEY (source_event_id, device_timestamp)` (§2.1).
  - `create_hypertable` trên `device_timestamp`, `chunk_time_interval => INTERVAL '1 day'`, `migrate_data => true`.
  - Index `ix_telemetry_measurement_site_device_time` xem lại: hypertable tự có index trên cột thời gian, nên index cũ có thể thừa một phần. Bỏ hay giữ thì **đo rồi quyết**, đừng đoán.
- `ADR-011` viết **trong commit này**: ba timestamp, vai trò từng cái, cái nào là trục phân mảnh, và vì sao cột `quality` của `scope.md` §8.3 không được dựng.
- Ghi lại **cú pháp thật** mà `timescale/timescaledb:2.29.2-pg17` chấp nhận (API `by_range(...)` hay dạng cũ) — plan này không đoán hộ.

**Kiểm chứng**
```bash
make ingestion-migrate && make test
```
Test bắt buộc (integration, PostgreSQL thật): sau migration, `timescaledb_information.hypertables` **có** bảng này; ghi một batch qua đường ingestion vẫn thành công; **dedup vẫn chặn** bản trùng (chạy lại đúng test của M2/C12, không viết test mới — nếu test cũ vẫn xanh thì việc đổi PK không làm hỏng hợp đồng); `Down` chạy được và bảng trở lại bảng thường.

> [!warning] Ba thứ có thể từ chối, và phải xử ngay tại chỗ chứ không vòng qua
> 1. **Khoá ngoại tới `ingest.processed_message`.** Nếu bản TimescaleDB này từ chối khoá ngoại trên hypertable (hoặc từ chối nén một bảng có khoá ngoại), **ghi lại thông báo lỗi thật**, rồi chọn: giữ khoá ngoại và bỏ nén cho tới khi nâng bản, hay bỏ khoá ngoại và để `ADR-030` là thẩm quyền duy nhất. Quyết định nào cũng được — **quyết định im lặng** thì không. Hệ quả vào `ADR-011` §Consequences.
> 2. **`migrate_data => true` khoá bảng.** Trên bảng đang trống thì tức thì; trên bảng đã có vài triệu row thì lâu. Chạy migration **trước** C08, đúng thứ tự trong bảng §4.
> 3. **Unique index nào còn sót** không chứa `device_timestamp` sẽ chặn `create_hypertable`. Đọc lại toàn bộ index của bảng trước khi chạy.

---

### C06 — `feat(ingestion): add compression and retention policies`

**Mục tiêu**: dữ liệu cũ co lại, dữ liệu quá hạn tự đi — và **biết** cái giá của việc đó.

**Việc làm**
- Migration `004_telemetry_policies.sql`: `compress_segmentby = 'site_id, equipment_id, signal_code'`, `compress_orderby = 'device_timestamp DESC'`, `add_compression_policy` sau **7 ngày**, `add_retention_policy` **400 ngày** — đúng `scope.md` §8.3/§8.4.
- Vì sao `segmentby` đúng là ba cột đó: nén theo cột chỉ hiệu quả khi các giá trị **cạnh nhau thì giống nhau**. Một kênh, một signal, xếp theo thời gian → điện áp kế tiếp gần điện áp trước. Trộn 1.000 kênh vào một segment thì phá đúng tính chất đó.
- **Lab: retention ăn mất dữ liệu của một đồng hồ sai** (§3.1). Chèn một row có `device_timestamp` 500 ngày trước, chạy retention job thủ công, đếm lại. Con số + kết luận vào `ADR-011` §Evidence.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: policy **có mặt** trong `timescaledb_information.jobs` với đúng khoảng; nén một chunk rồi vẫn `SELECT` ra đúng dữ liệu; **chèn được vào chunk đã nén** (dữ liệu về muộn phải vào được, không được lỗi) — và nếu bản này chèn được nhưng chậm, **đo độ chậm** thay vì ghi "chậm".

#### C06.1 — Nghiệp vụ: vì sao thô 400 ngày mà rollup 15 năm

Kỹ sư quy trình cần **từng mẫu** khi điều tra một sự cố — đường cong 18 giờ của đúng kênh đó, đúng ngày đó. Nhưng nhu cầu ấy hầu như luôn nằm trong **vài tháng** gần nhất: sự cố cũ hơn thì cell đã ra khỏi nhà máy.

Auditor thì ngược lại: hỏi về **năm ngoái**, đôi khi năm năm trước, và hỏi ở mức *"máy này ngày đó chạy trong khoảng nào"* — mức mà rollup 1 phút trả lời được.

Nên hai loại dữ liệu, hai vòng đời: thô **400 ngày** (`scope.md` §8.4), rollup **15 năm**. Đây không phải tối ưu dung lượng, mà là **hai câu hỏi khác nhau của hai người khác nhau**. Làm sai thì hoặc trả tiền lưu trữ cho dữ liệu không ai đọc, hoặc tới lúc auditor hỏi thì không còn gì để trả lời — và cái sau là mất chứng nhận.

---

### C07 — `feat(ingestion): add the one minute rollup and its refresh window`

**Mục tiêu**: rollup 1 phút, và cửa sổ refresh **tính ra được** chứ không chép từ scope.

**Việc làm**
- Migration `005_process_signal_rollup.sql`:
  - View `ts.process_signal` (§3.2).
  - Continuous aggregate `ts.process_signal_1m` trên **hypertable**, `time_bucket('1 minute', device_timestamp)`, `avg`/`min`/`max`/`count`, group theo `site_id, equipment_id, signal_code`, lọc `value_kind = 'real'`.
  - `add_continuous_aggregate_policy` với `start_offset` **tính từ ngân sách đến muộn** (§3.3), không phải 3 giờ chép sẵn.
- Một target refresh rộng (`make rollup-refresh-wide`) gọi `refresh_continuous_aggregate` trên vài ngày — chạy được bằng tay, và là chỗ M13 sẽ gắn lịch.
- `ADR-032` viết **trong commit này**, kèm **phép tính** ra `start_offset` và kèm điều kiện phải tính lại.

**Kiểm chứng**
```bash
make ingestion-migrate && make test
```
Test bắt buộc: ghi một batch → sau khi refresh, `sample_count` của bucket **bằng đúng** số row thô trong bucket đó; bucket biên (mẫu ở đúng giây 59,999) rơi vào **đúng một** bucket; rollup mang `site_id` và không có đường nào đọc nó mà không lọc site (K3).

> [!note] Đừng dựng rollup tầng hai ở đây
> Cám dỗ: dựng luôn rollup theo **máy** vì "kiểu gì cũng cần". C10 sẽ nói cần hay không, bằng số. Dựng trước là dựng một cấu trúc phải bảo trì mà chưa ai chứng minh nó giải quyết vấn đề gì.

---

### C08 — `feat(tools): backfill telemetry from the simulator curve model`

**Mục tiêu**: có bộ dữ liệu để chứng minh D1 và D2, **giống dữ liệu thật ở chỗ quan trọng**.

**Việc làm**
- Chế độ `backfill` (tham số: số kênh, số ngày, mốc kết thúc, chu kỳ mẫu) dùng lại `FormationChannel`/`FormationProfile` của simulator — **không** viết bộ sinh thứ hai. Đường cong CC/CV và nhiễu phải là **cùng code** đã chạy ở M2, nếu không thì tỉ số nén nói về một nhà máy không tồn tại (§3.4).
- Ghi bằng `COPY` (binary) vào **cả hai** bảng, `source_event_id` sinh bằng đúng `MeasurementNaturalKey` + `DeterministicGuid` của M1/C04.
- In ra và ghi lại: **dung lượng đĩa trống trước/sau**, tốc độ ghi **mỗi triệu row**, tổng thời gian, số row mỗi bảng.
- `make telemetry-backfill` với tham số, mặc định là bộ nhỏ để chạy trong CI được.

**Kiểm chứng**
```bash
make telemetry-backfill CHANNELS=8 DAYS=7
```
Test bắt buộc: chạy backfill **hai lần cùng tham số** → số row **không đổi** (khoá tự nhiên xác định, dedup vẫn đúng); mọi row telemetry có claim tương ứng; giá trị nằm trong dải vật lý của `FormationProfile`; `clock_quality` có **cả** `Good` lẫn `Drifted` nếu bật fault lệch đồng hồ.

#### C08.1 — Số phải ghi lại: `processed_message` chậm dần bao nhiêu

Đây là chỗ **duy nhất** trong M3 mà §2.5 được trả lời. Ghi thời gian ghi từng triệu row và đưa đường cong vào `benchmarks.md`; nếu nó gãy thì ghi rõ gãy ở mốc nào, và ghi kết luận vào `ADR-030` §Evidence.

Đừng "sửa" nó ở đây. Một btree UUID ngẫu nhiên chậm dần là **hệ quả đã biết** của một quyết định đã cân nhắc; việc của M3 là làm cho cái giá đó **có số**, để M9 quyết trên dữ liệu chứ không trên cảm giác.

---

### C09 — `test(ingestion): measure compression at two cardinalities`

**Mục tiêu**: **D1**, và chứng minh con số **có nghĩa**.

**Việc làm**
- `make compression-report`: với mỗi lượt, `hypertable_detailed_size` (và `chunk_compression_stats`) **trước** và **sau** khi nén, in **byte thật** cùng tỉ số.
- Chạy ở **hai** cardinality (ví dụ 8 kênh và ~100 kênh, cùng số ngày). Hai tỉ số lệch < 2 điểm phần trăm → được phép nói tỉ số này không phụ thuộc quy mô, và ghi câu đó **kèm hai con số**. Lệch nhiều hơn → **không được ngoại suy**, và phải chạy lượt thứ ba lớn hơn.
- Nếu đĩa cho phép, chạy thêm một lượt ở quy mô 24 giờ đầy đủ và ghi thành dòng thứ ba.

**Kiểm chứng**: đây là **D1**.
```bash
make compression-report
```
Ghi vào `benchmarks.md`: byte trước · byte sau · tỉ số · số chunk · cardinality · chu kỳ mẫu · và câu *"sinh bằng backfill, không đi qua MQTT/gateway"* — điều kiện đo là bắt buộc (`benchmarks.md` luật #2).

> Một tỉ số nén không kèm cardinality và chu kỳ mẫu là một con số không kiểm lại được. Cùng bảng đó với chu kỳ 1 giây thay vì 5 giây sẽ cho tỉ số khác — và người đọc lại sáu tháng sau sẽ không biết mình đang so hai thứ khác nhau.

---

### C10 — `test(ingestion): measure the seven day per minute query`

**Mục tiêu**: **D2**, đo ở cả hai cách đọc "máy X".

**Việc làm**
- `make rollup-bench`: chạy truy vấn 10 lần, in p50/p95/max, kèm `EXPLAIN (ANALYZE, BUFFERS)`.
- **Hai** truy vấn: một kênh trong 7 ngày, và **cả `FORM-01`** trong 7 ngày (§3.2).
- Chạy thêm **cùng truy vấn trên bảng thô** để có số đối chứng — con số cho biết rollup đáng giá bao nhiêu, và nó là thứ trả lời câu *"vì sao phải có continuous aggregate"* bằng số thay vì bằng niềm tin.
- `EXPLAIN` phải cho thấy **chunk exclusion** (không quét chunk ngoài 7 ngày) và **có đọc** continuous aggregate.

**Kiểm chứng**: đây là **D2**.
```bash
make rollup-bench
```
Nếu mức máy trượt 200 ms: dựng rollup tầng hai gộp theo máy, đo lại, và ghi **cả hai** bảng số. Đó là bằng chứng hiểu vấn đề, không phải dấu hiệu làm sai — nhưng phải theo thứ tự **đo trước, dựng sau**.

---

### C11 — `test(ingestion): count what the rollup misses when data arrives late`

**Mục tiêu**: **D5** — đóng lỗ hổng §2.2 bằng một phép đếm.

**Việc làm**
- `make rollup-reconcile`: với một khoảng thời gian **đã đóng** (cũ hơn `end_offset`), so `sum(sample_count)` của `ts.process_signal_1m` với `count(*)` trên bảng thô cùng điều kiện lọc. In hai số và hiệu.
- Kịch bản dựng lại đúng hành vi M2 đã chứng minh: ghi một lô row có `device_timestamp` **cũ hơn cửa sổ refresh** (mô phỏng gateway xả buffer sau outage), rồi đối chiếu **trước** và **sau** `rollup-refresh-wide`.
- Ba con số vào `benchmarks.md`: lệch ngay sau khi ghi · lệch sau refresh chính sách · lệch sau refresh rộng.

**Kiểm chứng**: đây là **D5**.
```bash
make rollup-reconcile
```
Test bắt buộc: sau refresh rộng, hiệu **bằng 0**. Và một test đối chứng chứng minh phép đếm này **bắt được lỗi**: cố tình đặt `start_offset` hẹp, ghi dữ liệu muộn, xác nhận hiệu **khác 0** — một phép đối chiếu không bao giờ đỏ là một phép đối chiếu chưa kiểm gì (bài học M2/C17).

---

### C12 — `feat(ingestion): archive the raw formation curve to minio with its digest`

**Mục tiêu**: giữ **bản gốc** của thứ quý nhất, và chứng minh được nó là bản gốc.

**Việc làm**
- Bucket `raw-curve` trong `minio-init`, tạo **kèm object lock** (§3.5). Hai bucket đang có là `evidence` và `vision` — cả hai đều không đúng mục đích này.
- Bảng `ts.raw_curve_archive`: `site_id`, `equipment_id`, `unit_id`, khoảng thời gian đường cong, `object_key`, `sha256`, `byte_size`, `recorded_at`. Bảng thường, không hypertable — mỗi đường cong một dòng, không phải mỗi mẫu một dòng.
- Upload file CSV gốc (từ backfill hoặc từ file-drop của M2/C15), tính `sha256` **lúc ghi**, không tính lại sau.
- Pin client S3 sau khi **kiểm license** (M1/C09), ghi kết quả vào `ADR-033`.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: upload rồi tải về → `sha256` **khớp**; sửa một byte trên đường tải về → phép kiểm **đỏ**; upload lại cùng đường cong → **không** tạo bản thứ hai; object lock **thật sự** bật (thử xoá → bị từ chối).

#### C12.1 — Nghiệp vụ: vì sao auditor không nhận dữ liệu đã qua xử lý

Hồ sơ traceability là **hồ sơ pháp lý** (`AGENTS.md` K4). Khi có tranh chấp — một lô pin cháy, một khách hàng ô tô đòi bằng chứng — câu hỏi không phải *"hệ thống của các anh nói gì"* mà *"máy đã ghi gì"*. Hai câu đó khác nhau đúng bằng mọi bước xử lý ở giữa: decode, gán equipment path, phân loại `clock_quality`, dedup.

Mỗi bước đó đều **có thể có bug**, và bug ấy có thể mới được sửa tháng trước. Bản gốc kèm `sha256` là thứ duy nhất cho phép trả lời *"đây là byte máy xuất ra, chưa ai chạm vào"* — và cho phép **chạy lại** toàn bộ chuỗi xử lý bằng code hôm nay để so.

---

### C13 — `feat(obs): add grafana with the first formation dashboard`

**Mục tiêu**: nhìn thấy đường cong. Không phải để đẹp — để **phát hiện dữ liệu sai bằng mắt**, thứ mà test không làm được.

**Việc làm**
- Grafana vào profile `obs` (§2.6), `it-net`, datasource PostgreSQL trỏ `timescale`, provisioning **bằng file trong repo** — không cấu hình bằng tay trong UI.
- Dashboard `formation-overview.json` **commit vào repo**, panel:
  - Đường cong formation của một kênh: `Formation/Voltage`, `Formation/Current`, `Formation/Temperature` (đọc từ rollup, chuyển sang bảng thô khi zoom hẹp).
  - Sample volume theo device time tính từ rollup — `sum(sample_count)` mỗi phút (§2.6). **Không**
    gọi nó là ingestion throughput: đại lượng đó phải nhóm theo `recorded_at`, không phải bucket của
    `device_timestamp`.
  - Đếm row `clock_quality <> 'Good'` theo giờ — badge cảnh báo mà `scope.md` §7.3 nói tới.
- Biến dashboard `site` bắt buộc, mặc định `NV1`. Một dashboard trộn site là phiên bản đọc của thứ K3 cấm.
- Kiểm license Grafana **trước khi pin** (nó là AGPL từ v8) và ghi vào commit message hoặc ADR — dùng nguyên container, không nhúng vào sản phẩm.

**Kiểm chứng**
```bash
make up-obs && make net-check && make grafana-net-check
```
`net-check` vẫn **9/9**; target riêng của Grafana phải **3/3**: container chỉ ở `it-net`, **tới được**
`timescale` và **không** tới được `emqx` (K11). Dashboard mở lên phải thấy **chỗ gãy CC → CV** trên
đường cong — [`glossary.md`](../glossary.md) nói thẳng: *"biểu đồ không có nó là biểu đồ của một máy
chưa sạc thật"*. Đây là phép kiểm bằng mắt mà không test nào thay được.

> [!warning] Grafana ở M3 **chưa có phân quyền**, và đó là một khoảng trống có thật
> Ai vào được Grafana thì đọc được dữ liệu **mọi site** — biến `site` là bộ lọc hiển thị, không phải authorization. Đúng thứ mà `scope.md` §7.5 phân biệt rạch ròi ở phía Mendix.
>
> **Không** vá tạm bằng cách khoá cứng một site vào datasource. Đường đúng là gắn Grafana vào Keycloak cùng lượt với phần bảo mật còn lại ở **M13**. `ADR-032` hoặc commit message ghi khoảng trống này thành một dòng để M13 nhặt — cùng cách M2/C08 đã ghi lại khoảng trống mTLS.

---

### C14 — `test(time): compare the naive date cast against the production calendar`

**Mục tiêu**: **lab phá hoại bắt buộc** của `scope.md` §9/M3 — đo sai số của cách làm sai.

**Việc làm**
- Trên dữ liệu backfill của `NV1`: tính sản lượng theo ngày hai cách — `CAST(device_timestamp AS date)` và `IProductionCalendar.GetProductionDay` — rồi đếm **bao nhiêu phần trăm row bị gán sai ngày**.
- Lặp lại trên một ngày ca C của `DE1` dựng bằng code lịch, gồm **cả** hai ngày DST. Ghi rõ vế nào là dữ liệu thật, vế nào dựng ra (§2.3).
- Con số vào `ADR-012` §Evidence và `benchmarks.md`.

**Kiểm chứng**
```bash
make calendar-lab
```
Kỳ vọng: ở `NV1` sai số đến từ **6 giờ mỗi ngày** bị gán nhầm — nhưng **đo rồi mới ghi**, đừng ghi trước con số mình đoán. Ở `DE1` phải thấy **thêm** một sai lệch chỉ xuất hiện ở hai ngày trong năm.

> Giá trị của lab này không nằm ở tỉ lệ phần trăm. Nó nằm ở chỗ: cách sai **không ném lỗi**, chạy nhanh hơn, và cho ra một bảng số trông hoàn toàn hợp lý. Đây là dạng lỗi mà chỉ có phép đối chiếu mới tìm ra — cùng họ với lab #1 và #2 của M2.

---

### C15 — `docs: close m3 with benchmarks and checklist`

**Mục tiêu**: đóng milestone bằng số.

**Việc làm**
- `docs/benchmarks.md` mục `## M3`, tối thiểu **10** dòng số thật: tỉ số nén ở cardinality 1 · ở cardinality 2 · byte trước/sau · p95 truy vấn 7 ngày mức kênh · mức máy · cùng truy vấn trên bảng thô · lệch của `rollup-reconcile` trước và sau refresh rộng · tốc độ backfill mỗi triệu row · dung lượng đĩa trước/sau · độ dài ca C ở bốn tổ hợp (2 ngày × 2 site) · tỉ lệ row bị gán sai ngày ở lab C14 · thời gian `make ci`.
- `scope.md` §8.3 sửa câu chữ theo §2.1 và §2.2 (tên bảng, cột `quality`, `start_offset`); §9/M3 sửa DoD D1 theo §2.4 và bỏ "nhiệt độ coating" khỏi dashboard theo §2.6; cập nhật Phụ lục A.
- `docs/oef-mapping.md`: dòng **Data Collection** → `xong`; thêm dòng cho tầng historian nếu bảng §5.2 còn thiếu.
- `ADR-011` §Evidence (số của C06), `ADR-012` §Evidence (số của C14), `ADR-030` §Evidence (số của C08).
- Điền checklist §7. Đổi `status` của plan này sang `done` — **chỉ khi cả 5 DoD đạt**.

**Kiểm chứng**: đọc lại `benchmarks.md` tìm ô nào ghi ước lượng thay vì số đo. Có một ô như vậy thì M3 chưa đóng được (`AGENTS.md` §1.3).

---

## 6. Rủi ro riêng của M3

| # | Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|---|
| R-M3-1 | `create_hypertable` bị từ chối vì index/khoá ngoại | Migration đỏ ngay lần đầu | C05: đọc hết index **trước**, và ghi lại thông báo lỗi thật rồi mới chọn đường vòng. Không bỏ khoá ngoại chỉ vì nó cản |
| R-M3-2 | Tỉ số nén đo trên dữ liệu hư cấu | Số đẹp bất thường (> 95%) hoặc tệ bất thường (< 40%) | §3.4 và C09: dùng đúng code đường cong, đo ở hai cardinality. Số đẹp quá cũng là dấu hiệu, không chỉ số xấu |
| R-M3-3 | Rollup nuốt dữ liệu về muộn | **Không có dấu hiệu** — đây là điều nguy hiểm nhất | D5 và C11. Một phép đối chiếu không bao giờ đỏ thì phải tự kiểm bằng kịch bản cố tình sai |
| R-M3-4 | `Europe/Berlin` không tra được vì `InvariantGlobalization` bị bật lại | `TimeZoneNotFoundException` — nhưng chỉ trên máy/container cấu hình sai | C02: test khẳng định cả hai ID IANA tra được, fail có thông báo. `ADR-020` |
| R-M3-5 | `production_day` tính bằng phép trừ 6 giờ trên UTC | Ba test đầu xanh; hai ngày DST đỏ — hoặc tệ hơn, cũng xanh vì test viết theo cùng logic sai | C02/C03: tính trong **giờ local**, và test DST viết **trước** khi sửa cài đặt |
| R-M3-6 | Retention xoá dữ liệu của thiết bị có đồng hồ sai | Không có dấu hiệu cho tới khi auditor tìm thấy lỗ hổng | C06 lab: đo, ghi vào `ADR-011` §Evidence, và ghi điều kiện mở lại quyết định |
| R-M3-7 | Đầy đĩa giữa lượt backfill | Postgres báo lỗi ghi, container có thể không tự phục hồi | C08 in dung lượng trống trước/sau; bắt đầu từ bộ nhỏ, tăng dần |
| R-M3-8 | Hết thời gian (đã biết trước: §4) | Ngày thứ 7 mà mới tới C08 | Cắt theo thứ tự: **C12 (MinIO raw curve) → M7** (formation saga là chỗ đường cong có chủ), rồi **C13 (Grafana) → M13**. **Không cắt** C01–C11 và C14: đó là cả ba DoD ★, D4, D5 và lab bắt buộc |
| R-M3-9 | Dashboard chỉ tồn tại trong volume của container | Sửa panel trong UI rồi mất sau `down -v` | C13: provisioning bằng file trong repo; sửa dashboard = sửa file JSON và commit |
| R-M3-10 | M3 trượt sang tính nghiệp vụ trên rollup | Xuất hiện cột `yield`, `oee`, hay một event mới | Callout §1. Rollup là hạ tầng đọc, không phải câu trả lời |

---

## 7. Checklist M3

Đánh dấu khi commit đã vào `main`.

| # | Commit | ☐ | Ngày | Ghi chú |
|---|---|---|---|---|
| C01 | `Nvm.Time` + định nghĩa ca | ☐ | | glossary làm trước |
| C02 | production day & shift theo múi giờ site | ☐ | | **D4** |
| C03 | hai ngày DST ở `DE1` | ☐ | | **D3**, ADR-012, đỏ trước xanh sau |
| C04 | múi giờ đọc từ factory model | ☐ | | |
| C05 | hypertable trên `device_timestamp` | ☐ | | ADR-011 |
| C06 | compression + retention policy | ☐ | | Lab đồng hồ sai → ADR-011 §Evidence |
| C07 | rollup 1 phút + cửa sổ refresh | ☐ | | ADR-032 |
| C08 | backfill từ code đường cong | ☐ | | Số `processed_message` → ADR-030 §Evidence |
| C09 | tỉ số nén ở hai cardinality | ☐ | | **D1** |
| C10 | truy vấn 7 ngày, hai mức đọc | ☐ | | **D2** |
| C11 | đối chiếu rollup vs thô | ☐ | | **D5** |
| C12 | raw curve lên MinIO + sha256 | ☐ | | ADR-033. Cắt được (R-M3-8) |
| C13 | Grafana + dashboard formation | ☐ | | Cắt được (R-M3-8) |
| C14 | lab `CAST(date)` vs lịch sản xuất | ☐ | | ADR-012 §Evidence |
| C15 | benchmarks + đóng M3 | ☐ | | |

**Definition of Done**

| # | Tiêu chí | ☐ | Bằng chứng |
|---|---|---|---|
| ★ D1 | Nén < 15% dung lượng gốc, tỉ số ổn định giữa hai cardinality | ☐ | |
| ★ D2 | Truy vấn 7 ngày mỗi phút < 200 ms (cả mức kênh lẫn mức máy) | ☐ | |
| ★ D3 | Hai ngày DST ở `DE1` đúng; ca C 9 giờ và 7 giờ | ☐ | |
| D4 | 05:59 vs 06:01 lệch đúng một `production_day`, ở cả hai site | ☐ | |
| D5 | Dữ liệu về muộn được gộp hoặc được đếm — lệch bằng 0 sau refresh rộng | ☐ | |

**Sản phẩm phụ bắt buộc**

- [ ] `make ci` xanh, số test tăng thật (M2 kết thúc ở **585**)
- [ ] `ADR-011`, `ADR-012`, `ADR-032`, `ADR-033` viết xong — mỗi cái trong commit ra quyết định
- [ ] `ADR-011` §Evidence có số của lab retention; `ADR-012` §Evidence có số của lab C14; `ADR-030` §Evidence có đường cong insert của `processed_message`
- [ ] `docs/glossary.md` có đủ cụm từ M3 *(làm trước, ở C01)*
- [ ] `docs/benchmarks.md` có ≥ 10 dòng số thật cho M3, không ô nào là ước lượng
- [ ] `docs/oef-mapping.md` dòng **Data Collection** chuyển sang `xong`
- [ ] `docs/event-catalog.md` **không đổi** — M3 không phát event
- [ ] `scope.md` §8.3, §9/M3 và Phụ lục A sửa theo §2.1, §2.2, §2.4, §2.6
- [ ] `make net-check` vẫn **9/9** sau khi thêm Grafana

> [!important] Câu hỏi "vì sao" cuối M3 (`AGENTS.md` §5.8.4)
> Trả lời **thành lời, không mở tài liệu**. Tắc câu nào thì phần đó chưa xong.
>
> 1. Vì sao hypertable chia theo `device_timestamp` chứ không theo `recorded_at`, và cái giá của lựa chọn đó là gì?
> 2. Một phép đo lúc 23:47 ngày 25/10 ở Leipzig thuộc ca nào, `production_day` nào — và vì sao câu trả lời năm nay khác cách tính "trừ 6 giờ"?
> 3. Dữ liệu về muộn hơn cửa sổ refresh thì nằm ở đâu, và ai phát hiện ra là nó thiếu?
> 4. Vì sao telemetry thô giữ 400 ngày mà rollup giữ 15 năm? Ai là người hỏi mỗi loại?
> 5. Vì sao vẫn phải giữ file CSV gốc trên MinIO khi mọi điểm của nó đã nằm trong DB?
> 6. Tỉ số nén 90% nói lên điều gì về **dữ liệu**, và điều gì nó **không** nói lên?

---

## 8. Sau M3

M4 (*Mendix nhập môn: Operator Station v1*) là milestone đầu tiên có **người dùng thật** nhìn vào dữ liệu này, và là chỗ `IProductionCalendar` rời khỏi test để vào một màn hình: *"ca này đã làm được bao nhiêu"*.

**Ba thứ M3 để lại cho M4 phải kiểm trước khi bắt đầu**:

1. **`IProductionCalendar` có được gọi từ ngoài `Nvm.Time` chưa**, hay nó vẫn là một thư viện chỉ có test gọi. Nếu chưa có hộ dùng thật thì API của nó chưa từng bị áp lực nào — và M4 sẽ là chỗ phát hiện nó thiếu phép mình cần.
2. **Rollup có đủ nhanh cho một màn hình auto-refresh không.** D2 đo một truy vấn chạy một lần; một WIP board 20 người mở cùng lúc là một bài toán khác. `scope.md` §4.1 xếp nó vào loại *live monitoring* 1–5 s — kiểm lại con số của C10 dưới góc nhìn đó trước khi hứa với UI.
3. **`ADR-023` mục *"Điều kiện kéo lên ĐÃ xảy ra"*** — K7 đóng ở **M4**, không phải M5: `IIdempotencyStore` bền vững phải có mặt **trong hoặc trước** commit đầu tiên của M4 có write. Đây là món nợ nặng nhất đang chờ ở milestone kế, và nó **không** phải việc của M3.

Đọc `scope.md` §7.5 (ba kênh Mendix ↔ backend), §9/M4, và `ADR-023` §Consequences trước khi lập plan M4.
