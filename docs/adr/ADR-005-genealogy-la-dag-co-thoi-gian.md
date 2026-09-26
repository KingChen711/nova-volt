# ADR-005 — Genealogy là DAG có thời gian, ghi bằng cạnh append-only

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.4, §8.2 · ADR-009 · ADR-044 · ADR-006 |

---

## Context

Một cuộn điện cực đi vào nhiều cell, một cell nhận nhiều lot, một cell có thể bị tháo khỏi module
này rồi lắp vào module khác. Quan hệ vì vậy không phải cây: một node có nhiều cha, và cạnh có thời gian
hiệu lực. Auditor hỏi được cả "pack này gồm gì hôm nay" lẫn "ngày 12/3 cell này nằm trong module nào".
K4 cấm UPDATE/DELETE hồ sơ traceability.

## Decision

- Genealogy là **DAG có thời gian**. Nguồn sự thật là event trong SQL Server: `MaterialLotConsumed`
  (stream `consumption:{serial}`), `UnitAssembledInto` / `UnitRemovedFrom` / `GenealogyCorrectionRecorded`
  (stream `membership:{serial}` của unit con), `RollCoated` (stream `roll:{rollId}`).
- Read model PostgreSQL `trace.genealogy_link` là bản sao tối ưu để đọc. Runtime role chỉ có
  `SELECT, INSERT`. Gỡ liên kết chỉ qua `trace.unlink`, hàm `SECURITY DEFINER` đặt `unlinked_at` đúng
  một lần kèm event nguồn. Sửa sai thêm cạnh CORRECTION và trỏ `superseded_by` từ cạnh cũ; không xoá dòng.
- Đoạn cuộn nằm ở `trace.roll_segment` với `numrange` và `EXCLUDE USING gist`: hai đoạn cùng cuộn,
  cùng mặt không chồng lấn, chặn ở DB.
- Chỉ chủ schema xoá được read model (`trace.reset_site`) để rebuild từ event store.

## Consequences

**Được**

- Rework và sửa sai giữ nguyên lịch sử; trạng thái tại một thời điểm dựng lại được từ
  `linked_at`/`unlinked_at`.
- Hàng trăm cell lắp vào một pack không tranh chấp stream của pack (ADR-044).

**Mất / phải chịu**

- Có hai bản dữ liệu (event và read model) và một độ trễ projection. Command không được đọc read model
  để quyết định ghi; command đọc stream membership trong SQL transaction.
- Rebuild cần credential chủ schema và một cửa sổ khoá projection của site.
- `trace.unlink` là một điểm UPDATE có kiểm soát, không phải append-only tuyệt đối. Nó chỉ đổi được
  cạnh đang hiệu lực, đúng một lần, và luôn kèm event nguồn.
