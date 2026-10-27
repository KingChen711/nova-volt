# ADR-054 — Foundation: model khai bằng attribute, handler đồng bộ, EF Core bên trong, schema sinh từ model

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-27 |
| **Liên quan** | ADR-051 · ADR-001 (SQL Server ở v1) · AGENTS.md K1, K2, K8, K9 · scope §3.1 |

## Context

R1 dựng phần đầu của Foundation: khai báo model, command pipeline, transaction, xóa mềm, facet, trace.
Các hành vi phải giống nền thật đã quan sát (scope §3.1): `Query` đọc trong transaction của một domain,
`ProjectionQuery` đọc ngoài transaction và chỉ thấy bản đã commit, `Submit` đẩy thay đổi vào transaction
để phần sau của chuỗi thấy, commit do nền làm ở cuối chuỗi, xóa mềm đặt `IsDeleted` bằng số lớn nhất + 1,
facet nằm ở bảng riêng với cột sparse, bảng mang tên cắt ngắn kèm hậu tố số. Cần chọn cách khai báo model,
cách truy cập dữ liệu và cách dựng schema.

## Decision

1. **Khai model bằng attribute generic** trỏ về một lớp đánh dấu functional block:
   `[FunctionalBlock("FB_OP_…", ModelDomain.OperationalData)]`, rồi `[Entity<TBlock>]`, `[Facet<TBlock>]`,
   `[Command<TBlock>]`, `[PreCheck<TBlock>]`, `[PostAction<TBlock>]`, `[Event<TBlock>]`, và
   `[AppCommand("App")]` cho composite. Domain của mọi thứ là domain của block. `ModelRegistry` quét
   assembly lúc khởi động và từ chối khai báo sai (pre-check khác domain với command, command không đúng
   một handler, event thiếu version), như bước biên dịch model của nền thật.
2. **Handler đồng bộ**: `TResponse Handle(TCommand, IPlatform)`, như handler của nền thật. Pre-check,
   post-action, composite cũng vậy.
3. **EF Core chỉ nằm trong Foundation.** `Query<T>()`/`ProjectionQuery<T>()` trả `IQueryable<T>` (BCL);
   handler không reference EF (K9). Model EF dựng động từ registry; bộ lọc `IsDeleted = 0` là query filter,
   nên đọc qua platform tự bỏ bản đã xóa còn SQL trực tiếp thì không. Facet của một entity là cây TPH với
   gốc `Facet<TEntity>`, cột phân biệt `FacetType`, một dòng cho mỗi (entity, facet).
4. **Schema sinh từ model** bằng `SchemaUpdater.UpdateDatabase` ("Update Database"): tạo bảng và cột còn
   thiếu, không xóa cột, ghi bảng tra `Foundation_ModelTable` (model → tên bảng). Không dùng migration EF.
   DB bật `READ_COMMITTED_SNAPSHOT`.
5. **Transaction theo domain.** Một chuỗi `CallCommand` cùng domain chung một transaction; `CallCommand`
   sang domain khác và mỗi lời gọi của composite có transaction riêng, commit ngay. `Submit` ghi xuống
   transaction ngay (`SaveChanges`) để `Query` phía sau thấy.
6. **Hai biến thể cho chỗ chưa xác minh** (scope §7), chọn bằng `SolutionConfiguration`:
   command con lỗi mà cha vẫn trả thành công thì có hủy transaction không (`ChildFailureDoomsTransaction`,
   mặc định không); commit hỏng thì response báo lỗi (mặc định) hay giữ kết quả handler (lab L02).

## Consequences

**Được**

- Handler đọc giống handler của nền thật: LINQ trên `platform.Query<T>()`, `Create`, `Submit`, `CallCommand`.
- Lỗi khai báo chặn khởi động, không đợi tới lúc chạy.
- Tên bảng và bảng facet giống hệ thật đủ để luyện SQL chẩn đoán (tra tên bảng, lọc `IsDeleted` từng bảng).

**Mất / phải chịu**

- Handler đồng bộ giữ một thread trong lúc chờ SQL. Chấp nhận: đây là bản mô phỏng, và số đo R1 cho
  p95 14,7 ms mỗi command (`docs/benchmarks.md`).
- `Submit` gọi `SaveChanges` mỗi lần: nhiều round-trip hơn gom một lần, đổi lại đúng ngữ nghĩa "phần sau
  của chuỗi thấy thay đổi".
- `READ_COMMITTED_SNAPSHOT` dùng tempdb cho version store.
- Facet một dòng cho mỗi (entity, facet) là lựa chọn của Foundation; tài liệu đã đọc chỉ nói facet ở bảng
  riêng với cột sparse, không nói một hay nhiều dòng mỗi entity.

**Việc phát sinh**

- NVM004: chặn `Query<T>` khác domain lúc build; `ModelMisuseException` chặn lúc chạy.
- NVM002 và NVM003 trỏ sang `Nvm.Foundation` (event, entity, facet), thay `Nvm.Contracts` của v1.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| File khai báo model riêng (kiểu file model của nền thật) rồi sinh C# | Thêm một bộ sinh code và một ngôn ngữ; không thêm hành vi nào phải luyện |
| Handler `async` | Lệch với handler của nền thật; không có hành vi nào trong scope cần nó |
| ORM tự viết, không EF | Phải tự dịch biểu thức LINQ; `Query` trả `IQueryable` là điều handler của nền thật dựa vào |
| Migration EF | Schema thành chuỗi script viết tay, trong khi hành vi cần luyện là "schema đi theo model" (lab L22) |
| Facet mỗi facet một bảng | Trái với quan sát "mọi facet của entity dùng chung bảng, cột sparse" |

## Evidence

- `tests/Integration/Nvm.FoundationTests`: 27 test hành vi xanh trên SQL Server 2022 (Testcontainers).
- `tests/Analyzers/Nvm.AnalyzerTests/Nvm004CrossDomainQueryAnalyzerTests.cs`; NVM004 báo đúng dòng
  cố ý sai trong `LabModel/LabCommands.cs` khi bỏ `#pragma`.
- `docs/benchmarks.md` mục R1.
