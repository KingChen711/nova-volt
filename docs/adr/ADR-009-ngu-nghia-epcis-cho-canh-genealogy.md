# ADR-009 — Ngữ nghĩa EPCIS cho cạnh genealogy, chiều cạnh theo dòng vật liệu

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.4 · ADR-005 · ADR-006 |

---

## Context

Scope §6.4 định nghĩa năm loại cạnh, mượn ngữ nghĩa GS1 EPCIS 2.0: TRANSFORMATION, ASSOCIATION,
AGGREGATION, SPLIT_MERGE, CORRECTION. Nếu gộp chúng, câu hỏi "pack gồm những gì" sẽ trả cả tray formation
và pallet.

Lần cài đặt đầu tiên ghi cạnh lắp ráp theo nghĩa EPCIS của `parentID` (thùng chứa là cha). Integration
test cho thấy forward trace từ lot ra pack trả rỗng. Lý do: lot → cell đi theo dòng vật liệu, còn
cell → module lại đi ngược chiều. Closure theo một chiều không nối được hai loại cạnh đó.

## Decision

- Chiều cạnh trong `trace.genealogy_link` là **dòng vật liệu**: `parent` là thượng nguồn (lot, cuộn,
  unit được lắp), `child` là hạ nguồn (unit tiêu thụ, unit chứa). Forward trace (recall) đi xuôi chiều
  cạnh, backward trace đi ngược.
- Tên field trong event giữ nghĩa nghiệp vụ: `UnitAssembledInto.ParentSerialNumber` là unit chứa.
  Projection đảo chiều khi ghi cạnh lắp ráp.
- `edge_kind`: 1 TRANSFORMATION (lot/cuộn → unit), 2 ASSOCIATION (cell → module → pack), 3 AGGREGATION
  (tray, pallet; không vào closure), 4 SPLIT_MERGE, 5 CORRECTION (thay cạnh 2 đã ghi sai).
- Closure và trace chỉ đi qua cạnh 1, 2, 4, 5.

## Consequences

**Được**

- Một lượt đọc closure theo một chiều trả lời cả recall (lot → pack) lẫn điều tra (pack → lot).
- AGGREGATION không làm bẩn câu trả lời "sản phẩm gồm gì".

**Mất / phải chịu**

- `parent`/`child` trong bảng cạnh khác nghĩa `parentID` của EPCIS với cạnh lắp ráp. Người đọc SQL phải
  biết quy ước này (ghi ở đầu migration `004-genealogy.sql`). Xuất EPCIS về sau phải đảo lại chiều.
- SPLIT_MERGE (slitting mother → daughter roll với ánh xạ toạ độ) mới có trong mô hình, chưa có event.
