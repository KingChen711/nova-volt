# ADR-044 — Mỗi cell, module, pack là một aggregate riêng; quan hệ là GenealogyLink

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.2, §9/M5 (lab phá hoại), ADR-001, ADR-042 |

---

## Context

Pack chứa tới 104 cell. Cách mô hình "tự nhiên" là một aggregate `Pack` chứa module và cell, vì
chúng là quan hệ cha–con. Khi đó mỗi phép đo trên một cell phải load cả pack, và mọi thao tác trên
dây chuyền của pack đó bị tuần tự hoá trên một stream.

Lab M5 đo đúng chuyện đó trên SQL Server 2022 (Testcontainers, Docker Desktop 16 CPU / 8 GB), qua
`SqlEventStore` và idempotency claim thật: 96 writer đồng thời, mỗi writer ghi 5 phép đo cho cell
của mình (480 command), mỗi command replay stream trước khi append.
Lệnh: `NVM_RUN_LABS=1 dotnet test --project tests/Integration/Nvm.IntegrationTests --filter-class Nvm.IntegrationTests.AggregateBoundaryLabTests --output Detailed`.

| Biến thể | Xung đột (ConcurrencyException) | p50 | p95 | p99 | Wall | Event phải replay |
|---|---:|---:|---:|---:|---:|---:|
| Một aggregate pack, đọc có khoá (UPDLOCK như store hiện tại) | 0 | 428 ms | 6.978 ms | **8.760 ms** | 11,1 s | 114.960 |
| Một aggregate pack, đọc optimistic + retry | **27.520** | 449 ms | 108.398 ms | **143.737 ms** | 149,8 s | 4.951.982 |
| Mỗi cell một stream + GenealogyLink riêng | 0 | 38 ms | 1.303 ms | **1.733 ms** | 2,0 s | 960 |

Một lần chạy, ngày 2026-09-26. Số tuyệt đối phụ thuộc máy; tỉ lệ giữa các dòng mới là kết luận.

## Decision

Mỗi cell, module, pack là một aggregate `ProductionUnit` riêng với stream riêng. Quan hệ lắp ráp là
`GenealogyLink` riêng cho từng cặp cha–con, append-only (scope §6.4), không nằm trong stream của pack.

## Consequences

**Được**

- 96 cell ghi đồng thời không tranh chấp nhau: 0 xung đột, p99 thấp hơn khoảng 5 lần so với biến
  thể có khoá và khoảng 80 lần so với biến thể optimistic.
- Chi phí load một command không tăng theo số cell trong pack (960 event replay so với 114.960).

**Mất / phải chịu**

- Câu hỏi "pack này gồm những gì" không trả lời được từ một stream; phải đọc read model genealogy
  (M6). Invariant xuyên nhiều unit (ví dụ pack đủ 96 cell) không còn được một aggregate bảo vệ trong
  một transaction; phải kiểm ở command lắp ráp hoặc bằng reconciliation.
- Tranh chấp không biến mất khi gộp aggregate mà chỉ đổi hình: store hiện tại khoá stream khi đọc
  trong transaction, nên tranh chấp thành chờ lock (p99 8,8 s) thay vì ConcurrencyException. Hai cách
  đều tuần tự hoá dây chuyền.
- p99 của biến thể tách stream vẫn 1,7 s trong lab. Nguyên nhân chưa tách riêng: 96 kết nối đồng thời
  cạnh tranh pool và claim idempotency trên cùng máy. Không dùng số này làm SLO.
