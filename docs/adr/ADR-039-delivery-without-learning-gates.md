# ADR-039 — Hoàn thiện toàn bộ dự án theo bằng chứng kỹ thuật

| | |
|---|---|
| **Status** | **Accepted** — chủ repo yêu cầu 2026-09-23 |
| **Date** | 2026-09-23 |
| **Liên quan** | [AGENTS.md](../../AGENTS.md) §0/§5 · [scope.md](../scope.md) §1.5/§9 · [tracker hoàn thành](../plans/project-completion.md) |

## Context

Repo bắt đầu là learning project: agent dừng sau từng đơn vị commit, chủ repo thao tác Mendix,
dự đoán trước lab và trả lời teach-back để đóng milestone. Cách làm đó đã để M2/M3 mở dù có
bằng chứng kỹ thuật lịch sử, đồng thời không đáp ứng yêu cầu mới của chủ repo: hoàn thiện toàn bộ
NovaVolt trước khi chuyển sang học cách làm thật trong Opcenter và quy ước công ty.

## Decision

- Agent triển khai, kiểm chứng và sửa findings liên tục qua M0–M13 trong phạm vi đã giao. M10–M12
  không còn là phần có thể bỏ hoặc rút gọn. Có thể chia lát cắt để review nhưng không dừng vì ranh giới
  commit; agent được tự commit/push phần đã kiểm chứng theo ủy quyền ngày 2026-09-23.
  Không phải tạo plan mới theo milestone; giữ scope/DoD, tracker ngắn và bằng chứng kiểm chứng.
- Teach-back, khả năng giải thích của chủ repo, dự đoán trước phép đo và thao tác Mendix thủ công là
  việc học tùy yêu cầu, **không** là điều kiện hoàn thành. Agent tự làm Mendix bằng công cụ được hỗ trợ;
  chỉ nhờ chủ repo phần UI không thể tự thao tác hoặc quyền truy cập chưa có.
- Giữ nguyên mọi DoD chức năng, ngưỡng số, K1–K13, yêu cầu lab kỹ thuật và review độc lập read-only.
  Lượt audit độc lập chỉ báo cáo; agent triển khai sửa finding và mời audit lại trong cùng phạm vi.
- Chỉ đánh dấu đạt khi có bằng chứng tái lập được và xác nhận code đang chạy đúng build. Bằng chứng
  cũ giữ nhãn lịch sử; việc bỏ learning gate không tự động xác nhận lại M2/M3 hay bất kỳ milestone nào.
  Các yêu cầu M9 về N1/N2, M12 về legal hold và M13 về soak liên tục 24 giờ vẫn là gate cứng.
- Ghi phần còn thiếu, phụ thuộc và blocker trong [tracker hoàn thành](../plans/project-completion.md).
  Chỉ hỏi chủ repo khi cần quyền/nguồn lực ngoài tầm agent hoặc muốn đổi contract cứng.

## Quan hệ với quyết định trước

Quyết định này thay các gate học tập và cách giao việc cũ trong `AGENTS.md`, `scope.md` và plan M2–M4.
Nó không sửa nội dung kỹ thuật của ADR đã Accepted, không miễn bất cứ ngưỡng nào của ADR-031/034,
và không coi số đo đã ghi trong `benchmarks.md` là phép đo mới.
