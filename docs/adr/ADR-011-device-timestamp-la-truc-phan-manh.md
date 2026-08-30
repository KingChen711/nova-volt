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
  vào chunk mà retention policy 400 ngày **xoá ở lần chạy kế tiếp** — mất một hồ sơ pháp lý, không lỗi,
  không cảnh báo. Đây là cái giá thật của quyết định này; số đo ở §Evidence.
- **Chunk không lấp đầy tuần tự.** Dữ liệu về muộn mở lại chunk cũ, nên compression policy phải chấp
  nhận ghi vào chunk đã nén (C06 đo cái giá đó).
- **Ghi song song vào một chunk chưa tồn tại thì deadlock.** Tạo chunk lấy
  `ShareUpdateExclusiveLock` trên hypertable, nên nhiều writer cùng với tới **cùng một** chunk mới sẽ
  tạo vòng chờ và PostgreSQL giết tất cả trừ một. Đây là **regression mà C05 tự sinh ra và phải tự
  đóng**: đường ghi retry có giới hạn khi gặp `40P01`/`40001` và **đếm** số lần retry. An toàn vì
  transaction bị giết đã rollback **cả claim lẫn telemetry** (`ADR-030`), và claim là
  `ON CONFLICT DO NOTHING`.
- **Rollback đắt.** Không có lệnh nào biến hypertable về bảng thường; script `Down` phải chép sang một
  bảng mới rồi đổi tên, tức chi phí tỉ lệ với dữ liệu.

**Việc phát sinh**

- Retention và compression policy (**C06**), kèm lab đo mất mát do đồng hồ sai.
- Một **cái đếm** row có `|device_timestamp − recorded_at|` lớn hơn một chunk. M3 dựng cái đếm, không
  dựng cột dẫn xuất.
- Đường ghi phải chịu được deadlock lúc tạo chunk — đã làm ở C05, và
  `IngestionMetrics.WriteRetryCount` là chỗ nhìn thấy nó.

**Điều kiện phải mở lại quyết định**: khi tỉ lệ row có `|device_timestamp − recorded_at|` vượt một
chunk lớn tới mức mất mát do retention không còn chấp nhận được, trục phân mảnh cần một **cột dẫn
xuất bị chặn hai đầu** (`least(greatest(device_timestamp, recorded_at − k), recorded_at + k)`) thay vì
đọc thẳng đồng hồ thiết bị. **M3 không dựng cột đó** — M3 dựng cái đếm để lần sau quyết bằng số.

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

Kết luận: hiện tượng chỉ xảy ra ở **ranh giới chunk**, tức một lần mỗi ngày cho mỗi hypertable, và
retry là cách xử đúng.

**Test**: `TelemetryHypertableTests` (3 test) — hypertable tồn tại và phân mảnh theo `device_timestamp`
mỗi 1 ngày; PK là `(source_event_id, device_timestamp)`; khoá ngoại còn nguyên; ba message cách nhau
ba ngày rơi vào **3** chunk; bốn writer tranh nhau tạo một chunk mới vẫn ghi **4.000 / 4.000** row;
`Down` đưa bảng về bảng thường, giữ nguyên row, PK và khoá ngoại.

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

Kết luận có giới hạn: bản 2.29.2 vẫn nhận dữ liệu về muộn đúng; hai lần năm cặp đại diện đo median
theo cặp chậm hơn **13,2 %** và **4,0 %**. Đây là số của local stack và workload 10.000 row, không phải hằng
số của TimescaleDB. Quan trọng hơn, sentinel `1 → 0` xác nhận rủi ro retention của trục
`device_timestamp` là mất dữ liệu thật và im lặng, không chỉ là suy luận.
