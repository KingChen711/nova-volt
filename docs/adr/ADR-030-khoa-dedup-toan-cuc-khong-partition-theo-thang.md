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
