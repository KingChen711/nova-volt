# ADR-011 — `device_timestamp` là trục phân mảnh của telemetry

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-30 |
| **Liên quan** | `ADR-002` (PostgreSQL + TimescaleDB), `ADR-030` (khoá dedup toàn cục), `ADR-012` (production day), `ADR-032` (dữ liệu đến muộn), [`scope.md`](../scope.md) §7.3, §8.3, §8.4, [`plans/M3-telemetry-timescaledb-production-calendar.md`](../plans/M3-telemetry-timescaledb-production-calendar.md) §2.1, §3.1, §C05 |

---

## Context

M2 để lại `ts.telemetry_measurement` là **bảng thường**. Comment trong
[`001_ingestion_dedup.sql`](../../src/Workers/Nvm.Ingestion/Migrations/Up/001_ingestion_dedup.sql) ghi
thẳng rằng việc chuyển nó thành hypertable thuộc M3. Mỗi row mang **ba** timestamp
([`scope.md`](../scope.md) §7.3):

| Trường | Nguồn | Đáng tin? |
|---|---|---|
| `device_timestamp` | Đồng hồ PLC | **Hay sai**, lệch được hàng giờ |
| `gateway_timestamp` | Lúc edge gateway nhận | Khá hơn, có NTP |
| `recorded_at` | Lúc hệ thống ghi nhận | Đáng tin nhất |

Hypertable chỉ chia được theo **một** cột thời gian, và cột đó quyết định:

- **chunk exclusion** — truy vấn nào loại bỏ được chunk trước khi đọc row (D2);
- **bucket của rollup 1 phút** — nhóm mẫu theo trục nào;
- **retention** — chunk nào bị drop khi quá 400 ngày.

Ràng buộc thật, đã đo ở M2: gateway có store-and-forward trên đĩa, và một outage 141 giây sinh
**7.717** phép đo về muộn thành một cục ([`benchmarks.md`](../benchmarks.md) §M2). Nghĩa là hai trục
lệch nhau **hàng giờ** là hành vi bình thường, không phải sự cố.

Điều đã biết lúc quyết mà chưa chắc đúng mãi: tỉ lệ thiết bị có đồng hồ sai. Fault injection của M2
đặt **10 %** thiết bị lệch, và đó là một con số cấu hình, không phải một phép đo trên nhà máy thật.

## Decision

**Hypertable `ts.telemetry_measurement` phân mảnh theo `device_timestamp`, `chunk_time_interval` một
ngày.** Cả ba timestamp vẫn được lưu và không cái nào bị ghi đè.

Primary key phát biểu lại thành **`(source_event_id, device_timestamp)`**, vì TimescaleDB đòi mọi
unique index chứa cột phân mảnh. **Điều này không làm yếu dedup**: thẩm quyền dedup của tầng thiết bị
là `ingest.processed_message` với `PRIMARY KEY (source_event_id)` toàn cục (`ADR-030`), và đường ghi
giành chỗ ở đó **trước** rồi mới ghi telemetry, trong cùng một transaction. PK của telemetry là lớp
phòng thủ **thứ hai** trước và sau M3; khoá ngoại giữ nguyên mệnh đề mạnh nhất — **không có row
telemetry nào không có claim**.

**Cột `quality SMALLINT` của [`scope.md`](../scope.md) §8.3 không được dựng.** Nó viết cho một nguồn
OPC UA mà dự án này chưa từng làm — đường thiết bị ở đây là Sparkplug B, và Sparkplug không có khái
niệm quality code của OPC UA. Thứ tồn tại là `clock_quality` (`Good`/`Drifted`/`Unknown`), tính từ
`|device_timestamp − gateway_timestamp|`, và nó trả lời một câu hỏi **khác hẳn**. Dựng một cột trông
giống `quality` rồi nhét `clock_quality` vào là tạo ra một trường mà sáu tháng sau không ai biết nghĩa
là gì.

`ts.process_signal` mà `scope.md` §8.3 mô tả như một bảng được dựng thành **view** trên bảng thô, và
rollup dựng **thẳng trên hypertable** — chi tiết ở C07 và `ADR-032`.

## Consequences

**Được**

- Mọi câu hỏi của kỹ sư quy trình đều theo **thời gian trên máy** (*"18 giờ formation của kênh này
  trông thế nào"*), và chỉ trục này cho chúng chunk exclusion.
- Rollup 1 phút gộp theo thời gian **quy trình** — thứ duy nhất có nghĩa vật lý. Trên trục
  `recorded_at`, một lần xả buffer 2 giờ nhét cả 2 giờ đường cong vào **một** bucket.
- Bảng thô vẫn chở được **cả năm** `value_kind`. `Formation/CellSerial` là chuỗi và
  `Formation/StepIndex` là số nguyên đếm bước; đổi sang hình dạng `DOUBLE PRECISION NOT NULL` của
  `scope.md` §8.3 là mất chúng.

**Mất / phải chịu**

- **Đồng hồ thiết bị sai thì row rơi vào chunk sai.** Sai vài giờ thì vô hại. Sai vài năm thì row rơi
  vào chunk mà retention 400 ngày **xoá ở lần chạy kế tiếp** — mất một hồ sơ pháp lý, không lỗi, không
  cảnh báo. Đây là cái giá thật của quyết định này; số đo ở §Evidence. **Hệ quả trực tiếp**: migration
  `007` **gỡ retention policy khỏi lịch** cho tới khi có legal hold (M12). Nửa xoá không được chạy khi
  nửa chặn chưa tồn tại — xem `007_retention_awaits_legal_hold.sql`.
- **Chunk không lấp đầy tuần tự.** Dữ liệu về muộn mở lại chunk cũ, nên compression policy phải chấp
  nhận ghi vào chunk đã nén (C06 đo cái giá đó).
- **Ghi song song vào một chunk chưa tồn tại từng tạo deadlock.** Nếu TimescaleDB tạo chunk sau khi
  transaction đã giữ claim, DDL lấy lock trên hypertable rồi quay lại bảng claim để chép foreign key;
  nhiều writer có thể tạo thành vòng chờ. Đường ghi hiện **pre-create từng daily slice bằng autocommit
  trước khi mở transaction claim + telemetry**, nên contract tại ranh giới chunk là
  `write_retries = 0`. Retry có giới hạn cho `40P01`/`40001` vẫn ở lại như safety net cho contention
  không liên quan; bất kỳ số retry nào khác 0 giờ là tín hiệu điều tra, không phải hành vi bình thường.
- **Rollback đắt.** Không có lệnh nào biến hypertable về bảng thường; script `Down` phải chép sang một
  bảng mới rồi đổi tên, tức chi phí tỉ lệ với dữ liệu.

**Việc phát sinh**

- Retention và compression policy (**C06**), kèm lab đo mất mát do đồng hồ sai.
- Một **cái đếm** row có `|device_timestamp − recorded_at|` lớn hơn một chunk. M3 dựng cái đếm, không
  dựng cột dẫn xuất. **Đã dựng**: `nvm.ingest.retention_risk`, tag `site_id` (K3), đọc ở
  `IngestionMetrics.RetentionRiskCount`. Ngưỡng là **một chunk = 1 ngày**, đúng
  `chunk_time_interval` của migration `003`, và **khác hẳn** `DriftedCount`: `Drifted` so đồng hồ
  thiết bị với đồng hồ gateway ở ngưỡng 5 phút để trả lời *"timestamp này có tin được không"*;
  `retention_risk` so đồng hồ thiết bị với lúc **ghi** ở bề rộng một chunk để trả lời *"row này có
  rơi vào chỗ retention với tới không"*. Một lần xả buffer 2 giờ sạch ở cả hai; một cycler báo năm
  2024 thì chỉ cái sau bắt được.
- Đường ghi phải tạo đủ slice trước claim và ép retry bằng 0 ở fixture song song qua nhiều ngày;
  `IngestionMetrics.WriteRetryCount` giữ vai trò cảnh báo nếu lock cycle khác xuất hiện.

**Điều kiện phải mở lại quyết định**: khi tỉ lệ row có `|device_timestamp − recorded_at|` vượt một
chunk lớn tới mức mất mát do retention không còn chấp nhận được, trục phân mảnh cần một **cột dẫn
xuất bị chặn hai đầu** (`least(greatest(device_timestamp, recorded_at − k), recorded_at + k)`) thay vì
đọc thẳng đồng hồ thiết bị. **M3 không dựng cột đó** — M3 dựng cái đếm để lần sau quyết bằng số.

**M12 bật lại retention theo điều kiện nào**: legal hold chặn được mọi retention policy, **và** tỉ lệ
`nvm.ingest.retention_risk` trên mỗi site đã được đọc trong một khoảng vận hành thật. Hai điều kiện,
không phải một — một cái chặn cố ý, một cái đo tai nạn.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Phân mảnh theo `recorded_at` | Tăng đơn điệu, chunk lấp đầy tuần tự, retention "thành thật". Nhưng rollup 1 phút mất nghĩa vật lý (một lần xả buffer 2 giờ vào một bucket) và truy vấn theo thời gian quy trình mất chunk exclusion — D2 gần như chắc chắn trượt |
| Hai trục: partition theo `recorded_at`, index theo `device_timestamp` | Trả tiền index trên **mọi** lần ghi để mua lại thứ phương án A có sẵn. Và vẫn phải chọn một trục cho rollup, tức vẫn phải trả lời đúng câu hỏi này |
| Cột dẫn xuất bị chặn hai đầu ngay từ M3 | Đúng về lâu dài, nhưng thêm một cột mà **chưa có số đo** nào nói ngưỡng chặn nên là bao nhiêu. Chọn sai ngưỡng là làm hỏng dữ liệu theo cách không đảo ngược được. Dựng cái đếm trước |
| Giữ nguyên bảng thường | Trượt D1 (không có nén) và D2 (không có chunk exclusion). Không phải một phương án, chỉ là không làm gì |
| Dựng lại đúng `ts.process_signal` của `scope.md` §8.3 | `value DOUBLE PRECISION NOT NULL` vứt bỏ `boolean`/`text`/`absent`, và cột `quality` là của một giao thức dự án này không dùng. View cho hình dạng đó mà không mất gì (C07) |

## Evidence

Đo ngày 2026-08-30 trên `timescale/timescaledb:2.29.2-pg17`.

**Cú pháp thật mà bản này chấp nhận** — plan không đoán hộ, đây là thứ đã chạy:

- `create_hypertable('ts.telemetry_measurement', 'device_timestamp', chunk_time_interval => INTERVAL '1 day', migrate_data => true)`
  — **chữ ký cũ vẫn chạy** trên 2.29.2, không cảnh báo deprecation.
- Khoá ngoại từ hypertable tới bảng thường: **được chấp nhận**, và vẫn giữ sau khi nén.
- `ALTER TABLE ... SET (timescaledb.compress, ...)` với `segmentby = 'site_id, equipment_id, signal_code'`:
  chạy, kèm `WARNING: column "source_event_id" should be used for segmenting or ordering`.
- **PK vẫn được ép trên chunk đã nén**: chèn một row trùng `(source_event_id, device_timestamp)` vào
  chunk đã `compress_chunk` → `ERROR: duplicate key value violates unique constraint`.
- Continuous aggregate **không** dựng được trên view:
  `ERROR: invalid continuous aggregate view / DETAIL: At least one hypertable should be used in the view definition.`

**Deadlock lúc tạo chunk** — bốn session psql cùng ghi 1.000 row vào **một chunk chưa tồn tại**:

- Lần 1 (chunk chưa có): **3 / 4** transaction bị giết, `ERROR: deadlock detected / DETAIL: Process
  71538 waits for ShareUpdateExclusiveLock on relation 27853` — `27853` là `ts.telemetry_measurement`.
  1.000 row vào được.
- Lần 2 (chunk đã có): **0 / 4** deadlock, 4.000 row vào đủ.

**Đính chính sau N-M3-10 (2026-09-01):** kết luận *"một vài retry ở ranh giới chunk là bình thường"*
ở trên không còn đúng. Lock graph tái hiện trực tiếp cho thấy **2/4** transaction chết ở đoạn
TimescaleDB thêm foreign key cho chunk sau khi claim đã được giữ. Trên fixture 16 ngày mới × 8 batch,
bỏ đúng lời gọi pre-create sinh **119 retry**; khôi phục pre-create cho **0 retry**, đủ **128 claim =
128 telemetry**, replay thành 128 duplicate và đúng 16 chunk. Năm lượt dựng database mới đều xanh.
Vì vậy retry chỉ là lớp chống sự cố thứ hai; pre-create ngoài transaction mới là phần sửa root cause.

**Test**: `TelemetryHypertableTests` ghim hypertable, trục một ngày, PK, foreign key và rollback;
`ParallelWriterDeduplicationTests.ConcurrentBatchesAcrossFreshDays_PrecreateEveryChunkBeforeClaiming`
ghim exact count, replay, 16 physical chunk và **`WriteRetryCount = 0`** qua 128 batch song song.

**Hợp đồng M2 không đổi**: `IngestionDeduplicationTests` và `ParallelWriterDeduplicationTests` chạy
**nguyên văn**, không sửa một dòng nào — 23/23 integration test xanh.

**Mất mát do retention trên đồng hồ sai**: xem C06, ghi bổ sung vào `benchmarks.md` §M3.

**Lab retention C06**, chạy ngày 2026-08-30 trên stack compose thật bằng
`make telemetry-policy-lab` sau migration `004_telemetry_policies.sql`:

- sentinel có `device_timestamp` chậm **500 ngày**: telemetry trước job = **1**, sau khi gọi đúng
  `policy_retention` job = **0**;
- claim tương ứng trong `ingest.processed_message` sau retention = **1** — retention không làm một
  replay cũ trở thành dữ liệu mới;
- thử insert lại chính claim đó: **0 claim mới**, nên telemetry không sống lại sau khi hết horizon;
- năm cặp chunk độc lập, mỗi vế preload cùng **50.001 row**, rồi mới nén treatment và timing một lô
  đến muộn **10.000 row**: median của năm tỉ số theo cặp = **1,132×**, tức chậm hơn **13,2 %** trong
  lần lab này; hai median biên là **527,895 / 597,392 ms**;
- trước mỗi lượt ghi treatment, `chunk_compression_stats` xác nhận **50.001 row** đã thành
  `Compressed`: **21.430.272–21.479.424 byte** trước nén còn **1.564.672–1.572.864 byte** sau nén;
- script tự chọn mười ngày chưa có chunk trong cửa sổ 30–300 ngày; mỗi trial có một control và một
  treatment riêng, nên không trial nào timing phần delta chưa nén do trial trước để lại.
- chạy lại trên bản script cuối có `SHARE` lock bảo vệ sentinel: median của năm tỉ số theo cặp =
  **1,040×**; hai median biên **643,808 / 735,849 ms**; năm treatment
  **21.413.888–21.512.192 → 1.572.864–1.581.056 byte** trước timing.

**Bằng chứng cho lựa chọn `segmentby`, từ D1/C15-2 (2026-08-31).** Bốn phép đo nén trên hai trục
tách rời cho thấy tỉ số nén bị chi phối bởi **cardinality**, gần như không bởi **số dòng**: đổi
cardinality 8 → 40 kênh làm tỉ số dịch **0,245687 pp**, còn nhân số dòng lên **4,001399×** ở cùng
cardinality chỉ dịch **0,004303 pp** — chênh nhau **57 lần**. Đây là số đứng sau câu *"nén theo cột
chỉ hiệu quả khi các giá trị cạnh nhau thì giống nhau"*: `segmentby = (site_id, equipment_id,
signal_code)` giữ mỗi segment thuần một chuỗi, và chính tính chất đó — không phải độ dài segment —
là thứ quyết định tỉ số.

Kết luận có giới hạn: bản 2.29.2 vẫn nhận dữ liệu về muộn đúng; hai lần năm cặp đại diện đo median
theo cặp chậm hơn **13,2 %** và **4,0 %**. Đây là số của local stack và workload 10.000 row, không phải hằng
số của TimescaleDB. Quan trọng hơn, sentinel `1 → 0` xác nhận rủi ro retention của trục
`device_timestamp` là mất dữ liệu thật và im lặng, không chỉ là suy luận.
