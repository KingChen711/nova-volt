# ADR-016 — CP-SAT theo cửa sổ cho matching cell → module

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.6, §9/M8 (N10, T6) · ADR-044 |

---

## Context

Module cần 12 cell cùng bin, spread capacity ≤ 0,8 Ah, OCV ≤ 10 mV, DCIR ≤ 0,15 mΩ, tối đa 3 lot điện cực,
cell không quá 30 ngày tồn kho, giữ dự phòng cho đơn ưu tiên, ưu tiên cell cũ (FIFO). DoD: 100.000 cell < 30 s (N10),
CP-SAT tạo nhiều module hơn greedy ≥ 8 % trên cùng dữ liệu (T6), property test 10.000 ca không vi phạm tolerance.

Một mô hình CP-SAT cho 100.000 cell không giải được trong 30 s. Lượt thử đầu (cửa sổ 96 cell theo OCV, 0,5 s,
presolve bật) cho **ít** module hơn greedy: phần lớn cửa sổ hết giờ trước lời giải đầu tiên (trạng thái Unknown).

## Decision

- `GreedyMatcher` (baseline scope): trong bin, xếp theo lot rồi capacity, cắt khúc 12; khúc hỏng thì bỏ cell đầu.
  Thêm biến thể `greedy-ocv` (xếp theo lot rồi OCV) chỉ để so sánh công bằng.
- `CpSatMatcher`: trong bin, xếp theo lot rồi OCV, cửa sổ 48 cell giải song song, tối đa 3 lượt trên cell dư (lượt
  sau lệch nửa cửa sổ). Mỗi cửa sổ: biến gán cell → slot, spread qua biến lo/hi có điều kiện, lot qua biến chỉ báo,
  tối đa số module rồi tuổi cell. Hint từ heuristic trong cửa sổ; hết giờ không có lời giải thì dùng hint.
  `num_workers:1`, 0,1 s/cửa sổ, **presolve tắt**. Mọi module ra khỏi matcher được kiểm lại bằng `ModuleRules`.
- Cân bằng site: matching chỉ đọc kho của site trong token (K3). Phần "không rút cạn một bin" được hiện thực bằng
  `ReservedPerBin` (giữ cell mới nhất) và `MaxModules` (trần theo phân bổ). Việc phân bổ giữa NV1 và DE1 là quyết
  định của người lập kế hoạch, ngoài matcher.
- Matching chỉ **đề xuất** (`POST /api/v1/matching/run`); lắp ráp thật vẫn đi qua `AssembleUnit`.

## Số đo (2026-09-26, 16 CPU, dữ liệu tổng hợp tất định `MatchingDataset`, một lần chạy)

| Thuật toán | Cell | Module | % tồn dư | Tuổi TB cell dùng | Thời gian |
|---|---:|---:|---:|---:|---:|
| greedy (baseline scope) | 97.371 | 1.546 | 80,95 | 10,03 ngày | 0,65 s |
| greedy-ocv | 97.371 | 4.776 | 41,14 | 9,97 ngày | 0,35 s |
| **cp-sat** | 97.371 | **7.498** | **7,59** | 10,19 ngày | **21,17 s** |

CP-SAT hơn baseline **+385 %**, hơn greedy-ocv **+57 %**. Trạng thái cửa sổ: Optimal 1.207, Feasible 1.390, Unknown 83.
Property test: 10.000 ca ngẫu nhiên (greedy-ocv và CP-SAT), 0 module vi phạm, mọi cell được tính đúng một lần.

Lab bỏ `MaxDistinctLots` (20.000 cell): số lot tối đa mỗi module 3 → 8, trung bình 1,25 → 1,30; số module bị một lot
ảnh hưởng trung bình 98,4 → 102,8, tối đa 105 → 111. Ràng buộc ít khi chặt vì cửa sổ đã xếp theo lot; nó vẫn cần
để giới hạn phạm vi recall khi dữ liệu trộn lot nhiều hơn.

## Consequences

**Được**: yield gần 92 % trong thời gian cho phép; FIFO không bị hy sinh (tuổi trung bình tương đương).

**Mất / phải chịu**

- Không phải tối ưu toàn cục: cell ở hai cửa sổ xa nhau không bao giờ gặp nhau. Chất lượng phụ thuộc cách chia cửa sổ
  và thời gian; số đo chỉ đúng cho phân phối tổng hợp này, chưa kiểm trên dữ liệu nhà máy.
- 21 s trên 16 CPU: máy ít nhân hơn sẽ vượt 30 s. Thời gian tăng tuyến tính theo số cửa sổ.
- Tắt presolve là tinh chỉnh theo đo đạc cho cửa sổ nhỏ, không phải khuyến nghị chung của OR-Tools.
