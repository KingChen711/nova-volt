; Diagnostic đã viết nhưng CHƯA phát hành. Chuyển sang Shipped.md khi cắt version.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
NVM001  | Nvm.Determinism | Error | Cấm đọc đồng hồ máy. Dùng TimeProvider (AGENTS.md K1)
NVM002  | Nvm.Contracts | Error | Cấm DateTime trong contract, kể cả lồng trong generic (AGENTS.md K2)
NVM003  | Nvm.Contracts | Error | Event phải có [EventVersion(n)], n >= 1 (AGENTS.md K6)
