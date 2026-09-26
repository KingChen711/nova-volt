# ADR-017 — Hold cascade là job có checkpoint trong SQL, mỗi chunk là một durable command

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.7, §9/M9 (N9, T7) · ADR-005, ADR-006, ADR-023, ADR-044 |

---

## Context

Hold một lot vật liệu phải giữ mọi unit hạ nguồn. Một lot điện dịch vào 3.000 pack là 315.000 unit (cell + module +
pack). N9: xong trong < 60 s, chạy hai lần như một lần, kill giữa chừng thì resume từ checkpoint, và ingestion không
giảm quá 10 % throughput trong lúc cascade chạy. Hold theo đoạn cuộn (`span`) chỉ được giữ đúng cell lấy vật liệu từ
đoạn đó.

Quality state của unit nằm trong FB Quality (facet `IUnitQualityFacet`, ADR-044). Danh sách hạ nguồn đến từ read
model genealogy (closure, ADR-006) — nằm ở PostgreSQL, eventual consistency so với event store SQL Server.

Lượt đo đầu (2026-09-26): 315.000 unit trong **63,92 s** — trượt. Nguyên nhân: mỗi chunk đọc lại cả stream
`cascade:{holdId}` để lấy expected version, nên chi phí tăng theo n².

## Decision

- `PlaceHold` ghi hold (bảng `quality.Holds`) và event `QualityHoldPlaced`. Không lan ngay trong request.
- `HoldCascadeWorker` (hosted service trong process Execution, bật/tắt bằng `NVM_QUALITY:CascadeWorker`) nhận hold
  Active chưa xong và chạy job:
  - `PlanHoldCascade` đọc hạ nguồn qua `IDownstreamUnits` (closure/span), chốt danh sách vào `quality.CascadeTargets`
    theo thứ tự cố định, và phát `HoldCascadeStarted`. Lập kế hoạch chạy **hai vòng** (`PlanRounds = 2`): vòng hai bắt
    các cạnh genealogy tới muộn trên read model; target mới được nối thêm, không đổi chỉ số của target cũ.
  - Mỗi `ApplyCascadeChunk(jobId, chunkIndex)` giữ tối đa **1.000** unit trong một transaction: ghi
    `quality.HoldMembers`, phát `UnitsHeldByCascade`, cập nhật checkpoint (`NextChunk`) và `StreamVersion` của job.
    Chunk cuối phát `HoldCascadeCompleted`.
  - Mỗi chunk là một **durable command** với idempotency key suy từ `{jobId}:chunk:{i}` (ADR-010, ADR-023): chạy lại
    một chunk đã commit chỉ phát lại outcome cũ. Đây là cơ chế idempotent và resume, thay cho `SKIP LOCKED`.
  - Expected version của stream cascade lấy từ `quality.CascadeJobs.StreamVersion`, không đọc lại stream.
  - `DelayBetweenChunks` là rate limit giữa các chunk (mặc định 0 trong lab).
- Unit "Held" là suy ra: có membership đang active của một hold Active. Thả hold xoá hiệu lực của membership chứ không
  sửa từng unit; event `QualityHoldReleased` cần chữ ký QaManager + ProductionManager, không ai là người đặt hold.

## Consequences

**Được**

- 315.000 unit trong 35,35 s (một lần chạy), chạy lại 0 chunk.
- Resume không cần mã riêng: worker đọc `NextChunk`, command của chunk đã commit trả outcome cũ.
- Span hold chỉ giữ cell có cạnh tiêu hao chồng lên đoạn lỗi (ground truth trong `QualityHoldTests`).

**Mất / phải chịu**

- Phụ thuộc read model: nếu projection genealogy chậm hơn cả hai vòng lập kế hoạch, unit tới muộn không bị giữ bởi
  job này. Cổng cuối vẫn còn: command tiêu hao/lắp ráp đọc quality facet trong transaction SQL và từ chối lot đang
  hold (`LOT_ON_HOLD`), nhưng unit **đã** lắp trước khi projection tới thì cần một lần lập kế hoạch sau nữa.
- Không có `SKIP LOCKED`: hai worker cùng chạy một job sẽ tranh cùng chunk; worker thứ hai chờ claim rồi nhận outcome
  cũ. Đúng nhưng lãng phí. Hiện chỉ một worker mỗi site.
- Worker chạy trong process Execution. Scope §6.7 đòi process riêng với ingestion — Execution đã tách khỏi Ingestion,
  nhưng chưa tách khỏi luồng command.
- **Phần "ingestion không giảm quá 10 %" của N9 chưa đo**: phải đo so với N1 đã nghiệm thu, và N1 còn chặn bởi rig
  (preflight 5.951 msg/s < 10.000). Con số 35,35 s chỉ là nửa thời gian của N9.

**Việc phát sinh**

- Lập kế hoạch lại theo lịch (hoặc khi projection báo đã qua mốc thời gian của hold) để bắt cạnh tới rất muộn.
- Đo N9 cùng tải ingestion sau khi rig qua preflight (M9 ★, requalify ở M13).
- Lab M9 #1 (cascade không chunk) và #2 (gộp quality/inventory state) chưa chạy.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Cascade đồng bộ trong request `PlaceHold` | Một transaction 315.000 dòng giữ lock nhiều phút; timeout HTTP; không resume được |
| Mỗi unit một command | 315.000 claim + outcome; ước tính vượt 60 s nhiều lần |
| Đọc stream cascade để lấy expected version mỗi chunk | Đã đo: 63,92 s, chi phí O(n²) |
| Quartz/MassTransit saga cho job | Thêm hạ tầng khi claim + bảng job SQL đã đủ; cùng lý do ADR-015 |

## Evidence

`QualityCascadeScaleLabTests` (NVM_RUN_LABS=1), SQL Server 2022 + PostgreSQL 17 Testcontainers, 16 CPU, một lần chạy
mỗi phiên bản:

| Phiên bản | Unit | Chunk | Thời gian cascade | Chạy lại |
|---|---:|---:|---:|---|
| đọc lại stream mỗi chunk | 315.000 | 315 | 63,92 s (trượt) | — |
| version trong `CascadeJobs` | 315.000 | 315 | **35,35 s** (8.911 unit/s) | 0 chunk, 0,00 s |

Seed 315.000 unit mất 306,8 s và không thuộc phép đo. `QualityHoldTests`: span 1250–1430 m giữ đúng C1, C2 và module
chứa chúng; kill sau chunk 0 rồi chạy lại xử lý đúng chunk 1–2; thả hold thiếu chữ ký hoặc do chính người giữ ký bị từ
chối; sửa một byte nội dung chữ ký → `FirstBroken` chỉ ra đúng vị trí.

Giới hạn: một lần chạy cho mỗi con số; chưa có tải ingestion song song.
