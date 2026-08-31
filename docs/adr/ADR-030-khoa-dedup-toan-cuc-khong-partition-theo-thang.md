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

Migration chạy bằng job `ingestion-migrate`, không tự chạy khi app HTTP khởi động (`scope.md` §8.4).

## Consequences

**Được**

- Một `source_event_id` chỉ có một chỗ trên toàn bộ lịch sử, kể cả delivery cách nhau nhiều tháng.
- Khoá dedup và row telemetry cùng commit hoặc cùng rollback. Ở tầng ingestion, giới hạn mà
  `ADR-023` nêu cho command handler trong RAM không còn tồn tại: effect ở đây cũng là row PostgreSQL.
- Cùng một đường ghi sẽ dùng lại cho CSV file drop ở C15; adapter không thể tự chọn định nghĩa dedup.

**Mất / phải chịu**

- Không drop partition cũ để dọn dedup key. Bảng này tăng theo số phép đo logic được giữ lại.
- Retention của telemetry **không được** tự động xoá dedup key tương ứng: xoá key sẽ làm một replay cũ
  trở thành dữ liệu mới. Nếu sau này buộc phải dọn, cần một chính sách horizon có bằng chứng rằng nguồn
  không thể replay xa hơn — không được suy từ retention của telemetry.
- Bảng khoá trở thành một global index có write contention. C16 phải đo nó ở tải ≥ 5.000 msg/s; nếu
  không đạt, mở lại quyết định bằng số đo thay vì hạ uniqueness.

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
