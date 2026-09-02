# ADR-030 — Khoá dedup toàn cục, không partition theo tháng

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `ADR-010` (UUIDv5 từ natural key), `ADR-023` (claim và effect cùng transaction), [`scope.md`](../scope.md) §7.2, [`plans/M2-simulator-ingestion-idempotency.md`](../plans/M2-simulator-ingestion-idempotency.md) §C12 |

---

## Context

Thiết kế ban đầu partition `ingest.processed_message` theo tháng của `first_seen_at`, với primary key
`(source_event_id, first_seen_at)`. PostgreSQL chỉ cho phép unique constraint trên một partitioned
table khi constraint chứa **mọi partition key**. Vì vậy hình dạng đó không thể ép uniqueness toàn cục
chỉ trên `source_event_id`.

Đây không phải góc cạnh hiếm. Gateway có store-and-forward, nên một phép đo cuối tháng có thể được
replay sau khi backend sống lại ở tháng kế tiếp. Nếu `first_seen_at` của hai delivery khác nhau, primary
key ghép coi chúng là hai row hợp lệ. Dedup báo thành công nhưng telemetry thừa một bản — đúng kiểu lỗi
không ném exception và chỉ lộ khi đối chiếu số lượng.

## Decision

`ingest.processed_message` là **bảng thường, không partition**, với:

```sql
PRIMARY KEY (source_event_id)
```

Mọi query và row vẫn mang `site_id` (K3). Index `(site_id, first_seen_at DESC)` phục vụ audit theo
plant và thời gian. Ingestion thực hiện `INSERT ... ON CONFLICT DO NOTHING`, rồi chỉ ghi telemetry cho
những id được `RETURNING`; cả hai statement nằm trong **cùng PostgreSQL transaction**.

Trước transaction đó, ingestion lọc những delivery đã có claim bằng cả `site_id` và
`source_event_id`, gom timestamp của phần còn lại theo slice một ngày, rồi gọi
[`_timescaledb_functions.create_chunk`](https://d1ovb29l12vjqm.cloudfront.net/api/latest/hypertable/create_chunk/)
cho từng slice trong statement autocommit. Việc này giữ DDL tạo chunk **đứng trước** mọi lock claim;
nhiều caller cùng slice là hợp lệ, chỉ một caller nhận `created = true`. Không cache kết quả vô hạn:
retention có thể xoá chunk sau đó. Delivery đã có global claim bị loại trước bước này để replay sau
retention không dựng lại một raw chunk rỗng.

Migration chạy bằng job `ingestion-migrate`, không tự chạy khi app HTTP khởi động (`scope.md` §8.4).

## Consequences

**Được**

- Một `source_event_id` chỉ có một chỗ trên toàn bộ lịch sử, kể cả delivery cách nhau nhiều tháng.
- Khoá dedup và row telemetry cùng commit hoặc cùng rollback. Ở tầng ingestion, giới hạn mà
  `ADR-023` nêu cho command handler trong RAM không còn tồn tại: effect ở đây cũng là row PostgreSQL.
- Cùng một đường ghi sẽ dùng lại cho CSV file drop ở C15; adapter không thể tự chọn định nghĩa dedup.
- Tạo TimescaleDB chunk không còn nằm trong transaction đã giữ claim, nên foreign key được TimescaleDB
  sao sang chunk mới không tạo lock cycle với những writer khác. Bounded retry vẫn giữ cho deadlock
  hoặc serialization failure không liên quan; nó không còn là cơ chế chính che việc tạo chunk sai thứ tự.

**Mất / phải chịu**

- Không drop partition cũ để dọn dedup key. Bảng này tăng theo số phép đo logic được giữ lại.
- Retention của telemetry **không được** tự động xoá dedup key tương ứng: xoá key sẽ làm một replay cũ
  trở thành dữ liệu mới. Nếu sau này buộc phải dọn, cần một chính sách horizon có bằng chứng rằng nguồn
  không thể replay xa hơn — không được suy từ retention của telemetry.
- Bảng khoá trở thành một global index có write contention. C16 phải đo nó ở tải ≥ 5.000 msg/s; nếu
  không đạt, mở lại quyết định bằng số đo thay vì hạ uniqueness.
- Mỗi batch có thêm một phép đọc claim và tối đa một lời gọi `create_chunk` cho mỗi ngày mới. Nếu
  validation phía database thất bại sau đó, slice rỗng có thể còn lại; đây là metadata có thể dùng lại,
  không phải claim hay telemetry giả.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Partition tháng, primary key `(source_event_id, first_seen_at)` | Không ép được điều cần ép: cùng id với hai thời điểm first-seen khác nhau đều hợp lệ |
| Partition tháng, unique index chỉ trên `source_event_id` | PostgreSQL từ chối vì unique constraint không chứa partition key |
| Hash partition theo `source_event_id` | Giữ uniqueness được, nhưng không giúp retention theo tháng — lý do duy nhất thiết kế ban đầu muốn partition; thêm vận hành mà chưa có số đo cho thấy cần |
| Một bảng global key + một bảng audit partition theo tháng | Đúng về correctness nhưng thêm dual structure và transaction work trước khi có nhu cầu audit đủ lớn |

## Evidence

Integration test dùng container `timescale/timescaledb:2.29.2-pg17` và migration thật:

- cùng message gửi **3 lần**, lần đầu tháng 1 và hai lần sau tháng 6 → `processed_message = 1`,
  `telemetry_measurement = 1`, duplicate counter = **2**;
- hai message chỉ khác `device_timestamp` → **2** khoá và **2** row telemetry;
- constraint telemetry cố ý fail sau khi claim đã insert → **0** khoá và **0** telemetry của message đó.

Lệnh: `dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj` —
**1/1 test xanh**, 2026-08-28.

### M3/C08 — đường cong ghi của global key

Backfill đo riêng thời gian binary `COPY` vào `ingest.processed_message`, không lấy tổng thời gian
tool rồi gán hết cho btree. Lệnh tạo **3.063.893** row mới từ đúng `FormationLine`/RBE:

```bash
make telemetry-backfill CHANNELS=40 DAYS=7 SAMPLE_PERIOD_SECONDS=5 \
  DRIFTED_RATE=0.25 END_AT=2026-08-08T00:00:00Z
```

`processed_message` có **6.127.588** key trước lượt và **9.191.481** key sau lượt. Ba interval
đủ một triệu row cho kết quả:

| Interval | Thời gian `COPY` claim | Tốc độ |
|---|---:|---:|
| triệu mới thứ 1 | 10,477296 s | 95.444,475 row/s |
| triệu mới thứ 2 | 11,192841 s | 89.342,824 row/s |
| triệu mới thứ 3 | 10,821686 s | 92.407,046 row/s |

**Kết luận đo được**: ở dải **6,13 → 9,19 triệu key**, phase ghi global UUID btree chưa có
đường giảm đơn điệu. Điểm thấp nhất thấp hơn điểm đầu **6,39 %**, nhưng điểm thứ ba hồi lại lên
92.407 row/s. Chưa có bằng chứng để nói index đã “gãy”, và cũng không được ngoại suy rằng nó sẽ
phẳng ở 100 triệu key. Phép đo này là batch `COPY`, nên không thay phép đo đường ingestion đồng thời
ở tải ≥ 5.000 msg/s.

Tổng end-to-end của ba triệu lại giảm 4.109 → 3.475 → 3.095 row/s. `pg_stat_activity` cho thấy
backend đang chạy query verify 50.000 cặp ID–timestamp, không phải chờ phase `COPY`; telemetry
`COPY` đồng thời gần như phẳng ở 21.769 → 21.388 → 21.896 row/s. Vì vậy C08 sửa verifier theo hai
điều đã có bằng chứng:

- row mới lấy chính row-count trả về từ hai lệnh `COPY`; foreign key và transaction vẫn bảo đảm
  không có telemetry thiếu claim;
- row trùng mới query lại telemetry, với min/max `device_timestamp` tường minh để TimescaleDB loại
  chunk ngoài khoảng. Cùng 5.470 duplicate giảm từ **13,350 xuống 1,278 giây**, và vẫn verify đủ
  5.470 claim cùng 5.470 telemetry.

Dataset chuẩn 8 kênh × 7 ngày được chạy hai lần: lượt đầu **612.482 inserted**, lượt hai
**0 inserted / 612.482 duplicate**; cả hai lượt đều báo claim và telemetry **612.482 / 612.482**.
Integration test trên database trắng còn cố ý làm telemetry vi phạm constraint để transaction rollback
cả claim. Các số chi tiết, điều kiện máy và dung lượng trước/sau nằm trong
[`benchmarks.md`](../benchmarks.md) mục M3.

### M3/C15-2 — cùng câu hỏi, ở gấp bốn lần quy mô, và câu trả lời đổi

Lượt sinh dữ liệu của D1 (`ADR-034`) tình cờ là phép đo mà mục trên còn thiếu: **13.130.541** row
liên tục, đưa `ingest.processed_message` từ **20,80 triệu** lên **33,93 triệu** key trong *một* lượt —
dải rộng hơn C08 bốn lần, và ở vùng số liệu C08 chưa với tới.

Mười ba mốc triệu liên tiếp, ba số cho mỗi mốc:

| Mốc triệu | `COPY` claim | `COPY` telemetry | End-to-end |
|---|---:|---:|---:|
| 1 | 11,098 s | 48,789 s | **95,270 s** |
| 7 | 9,127 s | 46,099 s | **100,011 s** |
| 12 | **8,730 s** | 44,908 s | **116,747 s** |
| 13 | 10,189 s | 46,110 s | **126,817 s** |

**Kết luận đo được, và nó ngược với thứ ADR này lo:** end-to-end chậm đi **33,1 %**, nhưng phase ghi
btree UUID toàn cục **không chậm đi** — nó phẳng, và điểm nhanh nhất của cả lượt (8,730 s) nằm ở mốc
thứ **12**, tức lúc bảng đã lớn nhất. Telemetry `COPY` cũng phẳng. Phần dư ngoài hai phase `COPY` đi
từ **35,38** lên **70,52 s/triệu** — nó **gấp đôi**, và nó là toàn bộ phần chậm đi.

Ba hệ quả:

1. **Ở dải 6,13 → 33,93 triệu key, chưa có đường cong suy giảm nào của global btree.** Mệnh đề
   *"insert vào một btree UUID ngẫu nhiên đang lớn dần là đường cong suy giảm kinh điển"* vẫn đúng về
   lý thuyết; ở quy mô này nó **chưa xảy ra**, và giờ có số nói vậy thay vì có niềm tin nói vậy.
2. **M9 mà tối ưu khoá dedup dựa trên con số end-to-end thì sẽ tối ưu nhầm chỗ.** Đây là lý do C08
   tách riêng thời gian `COPY` ngay từ đầu, và là lý do phải tiếp tục tách.
3. **Không được ngoại suy tới 100 triệu key.** 33,93 triệu vẫn nhỏ. Điều phép đo này bác bỏ là *"nó
   đang gãy rồi"*, không phải *"nó sẽ không bao giờ gãy"*.

Điều kiện đo, `benchmarks.md` §M3 giữ đầy đủ: `CHANNELS=40 DAYS=30 SAMPLE_PERIOD_SECONDS=5
DRIFTED_RATE=0.25 CLOCK_DRIFT_HOURS=2`, binary `COPY`, mọi mốc trong cùng một process và cùng hình
dạng transaction.

### M3/N-M3-10 — tạo chunk trước claim, không tăng retry

Tái hiện trực tiếp trên TimescaleDB `2.29.2-pg17` với bốn transaction đồng thời cho **2/4** transaction
thoát `40P01`. Server context chỉ đúng câu DDL TimescaleDB đang chạy:
`ALTER TABLE _hyper... ADD CONSTRAINT ... REFERENCES ingest.processed_message`. Lock cycle là:
writer đã giữ `RowExclusive` trên bảng claim, một writer tạo chunk giữ `ShareUpdateExclusive` trên
hypertable, rồi DDL sao foreign key xin `ShareRowExclusive` trên bảng claim.

Regression test chạy **16 ngày mới × 8 batch đồng thời/ngày = 128 row**. Negative control chỉ bỏ lời
gọi pre-create làm test đỏ với **119 write retry**; khôi phục đúng một thay đổi đó cho **0 retry**,
`128 claim = 128 telemetry`, replay toàn bộ thành **128 duplicate**, và vẫn đúng **16 chunk**. Một lượt
đại diện có p95/max **323/323 ms**. Test đích xanh **5/5** trên năm database mới; toàn integration suite
chạy parallel mặc định xanh **5 × 50/50 = 250/250**. `make ci` Release sau đó xanh **686/686** và
buffer-crash **0/200**.
