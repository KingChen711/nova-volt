# ADR-006 — Closure table có đếm đường đi cho forward trace

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.4, §8.2, N6/N7/N11 · ADR-005 · ADR-009 |

## Context

Recall cần forward trace từ lot ra pack với p95 < 200 ms (N6); backward p95 < 150 ms (N7); rebuild < 10 phút (N11).
Lab `GenealogyScaleLabTests` (NVM_RUN_LABS=1), 2026-09-26, SQL Server 2022 + PostgreSQL 17 Testcontainers, Docker 16 CPU/8 GB:
100.000 cell → 8.000 module → 1.000 pack, mỗi cell 4 nguồn (2 cuộn có span, 2 lot). 504.200 event; 504.000 cạnh; closure 636.407 dòng (117 MB).

| Truy vấn (200 mẫu) | p50 ms | p95 ms | max ms |
|---|---:|---:|---:|
| Forward lot/cuộn → pack, closure | 1,43 | **2,28** | 3,40 |
| Forward, recursive CTE | 21,60 | **211,17** | 294,08 |
| Backward pack → mọi tổ tiên, closure | 1,29 | **1,62** | 2,90 |
| Backward, recursive CTE | 2,72 | 3,91 | 6,10 |
| Span cuộn [250, 430) | 1,71 | 2,47 | 5,54 |

Seed 184,6 s; rebuild set-based 65,8 s. Một lần chạy.

## Decision

Forward và backward đọc `rm.genealogy_closure` (cặp tổ tiên–hậu duệ, `paths` đếm số đường đi để gỡ một cạnh trong DAG không xoá quan hệ còn đường khác). Rebuild quét feed một lượt, COPY cạnh, tính closure bằng một recursive CTE.

## Consequences

**Được**: forward p95 2,3 ms so với 211 ms của CTE (CTE vượt N6 ở quy mô này); backward cả hai cách đều đạt N7.

**Mất / phải chịu**: closure chiếm dung lượng tương đương bảng cạnh; mỗi cạnh mới ghi anc×desc dòng; gỡ cạnh phải trừ đúng số đường đi. Backward chưa cần closure ở quy mô này — giữ vì dùng chung một cơ chế.
