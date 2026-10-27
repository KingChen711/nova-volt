# ADR-053 — Giao diện v2 bằng Next.js, thay Mendix

| | |
|---|---|
| **Status** | **Accepted** — chủ repo chốt 2026-10-27 |
| **Date** | 2026-10-27 |
| **Liên quan** | ADR-051 · ADR-014 (Mendix ở v1) · AGENTS.md K10 |

## Context

v1 chốt toàn bộ giao diện bằng Mendix (ADR-014). Màn Mendix của M4–M12 bị hoãn từ 2026-09-28 vì
Studio Pro MCP không kết nối được; mỗi thao tác Studio Pro không có công cụ phải giao lại cho chủ
repo. Với v2, giao diện cần đi cùng từng luồng nghiệp vụ để DoD của mốc gồm cả chạy luồng trên màn.

## Decision

Giao diện v2 là một web Next.js (App Router, TypeScript) trong `web/`, chia khu theo đúng cách hệ
thật chia app (mỗi khu một nhóm người dùng), menu và quyền theo role trong token Keycloak. Web chỉ
gọi OData và command API của MOM (K10), không nối thẳng database, không giữ logic nghiệp vụ, và hiện
nguyên mã lỗi cùng câu lỗi trong thân response. Khung web dựng ở R2; từ R4 mỗi mốc làm màn của luồng
mình.

## Consequences

**Được**

- Agent tự làm trọn giao diện, không cần Studio Pro hay MCP; lab Playwright chạy trực tiếp.
- Màn đi cùng backend trong từng mốc.

**Mất / phải chịu**

- Không còn luyện Mendix trong repo này, trong khi hệ thật dùng nhiều app Mendix. Kỹ năng lần một màn
  Mendix xuống logic phía sau phải luyện ở chỗ khác.
- Thêm một stack (Node, TypeScript) vào CI và tài liệu chạy local.

**Việc phát sinh**

- Bỏ quy tắc "toàn bộ UI bằng Mendix" và §5.6 Mendix của AGENTS.md; `mendix/` của v1 ở lại tag `v1.0`.
- Job CI cho web khi `web/` xuất hiện (R2).

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Giữ Mendix | Phụ thuộc MCP và thao tác tay; giao diện bị dồn về cuối |
| Blazor | Chủ repo chọn React/Next.js |

## Evidence

Quyết định theo lựa chọn của chủ repo, không dựa trên phép đo.
