# Ánh xạ Opcenter Execution Foundation → repo này

Mỗi khái niệm của Opcenter Execution Foundation có một thứ tương ứng phải **tự tay dựng** trong
repo. Bảng này là thước đo tiến độ thật của mục tiêu học — khác với checklist milestone, thứ chỉ
đo tiến độ code.

Nguồn: `docs/scope.md` §5.2. Cột **Trạng thái** là phần thêm, chỉ tồn tại ở đây.

## Cách dùng

Cập nhật **hai lúc**, không đợi cuối milestone:

- Học xong một bài trong course *"Opcenter Essentials for Developers"* → đối chiếu lại xem thành
  phần tương ứng trong repo có còn đúng không. Học xong thường làm lộ ra rằng cột *"Thành phần
  trong dự án"* đang mô tả sai.
- Làm xong một thành phần → đổi trạng thái, ghi commit vào cột **Ghi chú**.

| Trạng thái | Nghĩa |
|---|---|
| `chưa làm` | Chưa có gì. Thư mục rỗng cũng là `chưa làm` |
| `đang làm` | Có code chạy được nhưng chưa đủ để nói là hiểu khái niệm đó |
| `xong` | Dựng xong **và** giải thích được vì sao Opcenter làm như vậy |
| `khác scope` | Đã quyết định không dựng. Phải kèm lý do, và nên có ADR |

> `xong` đòi hỏi vế thứ hai. Dựng được mà không nói được vì sao thì vẫn là `đang làm` — vì mục
> tiêu của dự án là hiểu mô hình, không phải có thư mục đúng tên.

---

## Bảng ánh xạ

| Khái niệm OEF | Bài trong course | Thành phần trong dự án | File / thư mục | Trạng thái | Ghi chú |
|---|---|---|---|---|---|
| **Bus-Centric Design** | 1. Key Design Principles | RabbitMQ + MassTransit, mọi giao tiếp liên-FB đi qua bus | `src/Platform/Nvm.Bus/` | `đang làm` | **Vế máy kiểm được: xong. Vế "giải thích được": chưa** — D5 của M1 còn mở, nên theo đúng luật hai vế ở đầu file này, ô vẫn là `đang làm`. M1 hết C14. Một publish → **2 consumer, 2 queue riêng** (C13). Lab phá hoại cho con số giải thích **vì sao** bus-centric là điều kiện của N15: **18/200 event mất** khi broker chết 30 s, app **không restart lần nào**, `/health/live` xanh suốt (`benchmarks.md`, `ADR-022`) |
| **Manufacturing Service Bus** | 1, 6. RabbitMQ Configuration | Topology exchange/queue, retry, DLQ, delayed message | `src/Platform/Nvm.Bus/Topology/` | `đang làm` | **Vế máy kiểm được: xong. Vế "giải thích được": chưa** (D5 còn mở). Exchange theo context + routing key `nvm.{site}.{context}.{event}.v{n}` (C10); retry đo được **5 lần / 245–480–920–1933 ms** có jitter (C11); **1 message vào `_error`, queue chính còn 0** (C13). **Delayed message CỐ Ý không làm** — plugin không có trong image, và `scope.md` §5.7 đã nêu **hướng dự kiến** là Quartz store cho saga ở M7; `ADR-015` sẽ được viết ở M7, chưa tồn tại (`Nvm.Bus/README.md`) |
| **SOA layers** | 1 | Horizontal (Platform) + vertical (Functional Block) | `src/Platform/` vs `src/FunctionalBlocks/` | `đang làm` | Platform có `Nvm.Contracts`, `Nvm.Kernel`, `Nvm.Bus`; FunctionalBlocks có `FactoryModel`. Ranh giới được **ép bằng NetArchTest** (C17). Chưa `xong` vì mới có **một** FB — quy tắc "FB không reference FB" chưa có gì để vi phạm |
| **Event-Driven Architecture** | 1 | CloudEvents envelope + domain event trên bus | `src/Platform/Nvm.Contracts/` | `đang làm` | Envelope §7.4 + `IDomainEvent` + `[EventVersion]` + golden file (C01–C03); thuộc tính CloudEvents đi ở transport header trên message thật (C12, `ADR-008`). Còn thiếu **event store** và **upcaster** — đó mới là phần chứng minh đọc được event 15 năm sau, và cả hai thuộc **M5** |
| **Domain-Driven Design / bounded context** | 1, 17. Domain within OEF | Mỗi Functional Block = một bounded context có schema DB riêng | `src/FunctionalBlocks/<Name>/` | `đang làm` | `FactoryModel` là bounded context đầu tiên, có từ vựng riêng và exchange riêng trên bus. **Chưa có schema DB riêng** — chưa có DB nào (M5) |
| **Functional Block** | 9. Functional Block Artifacts | Class library có `Entities/`, `Commands/`, `Handlers/`, `Facets/`, `Events/`, `Migrations/` | `src/FunctionalBlocks/FactoryModel/` | `đang làm` | FB đầu tiên có `Entities/`, `Commands/`, `Handlers/`, `Seeding/`, `Storage/`. Thiếu **`Facets/`** và **`Migrations/`** — hai thứ đó cần event store và DB, tức M5 |
| **Entity** | 9, 18–19 | Aggregate root / entity trong FB | `.../Entities/FactoryNode.cs` | `đang làm` | `FactoryNode`, `FactorySite`, `FactoryModelSnapshot` — bất biến, invariant ép bằng kiểu (`EquipmentPath` quyết định bậc). Chưa phải **aggregate root event-sourced** (M5) |
| **Facet** (mở rộng entity) | 18–19 | Bảng mở rộng + interface `IFacetOf<TEntity>` | `.../Facets/` | `chưa làm` | M5 |
| **Command / Extended Command** | 18–19 | `record XxxCommand : ICommand` | `.../Commands/` | `đang làm` | `ICommand<TResult>` + `ActivateFactoryModelRevisionCommand`, natural key đặt cạnh command (`KeyFor`). **Extended command** — FB khác thêm command vào FB này — chưa có gì để thử |
| **Command Handler** | 18–19 | `ICommandHandler<TCommand>` + pipeline behavior | `.../Handlers/` | `đang làm` | Dispatcher + **3/4 behavior**: Validation → Idempotency → Audit. Idempotency **giành chỗ trước** khi gọi handler (`Claim`/`Complete`/`Abandon`), nên thứ tự Validation → Idempotency là correctness **thật** chứ không phải preference. Bảo đảm hiện có là **process-local**: duplicate tuần tự và duplicate đồng thời trong cùng một process chỉ tạo **một** effect, và mọi nhánh lỗi `Abandon` để khoá mở lại. **Chưa đạt**: restart (store mất sạch), nhiều instance (hai process không thấy nhau), và atomicity giữa idempotency record với business effect. **K7 vì thế CHƯA ĐÓNG** — cần cả **`TransactionBehavior`** lẫn một store bền vững, và cả hai cần database: **M5** chịu trách nhiệm (`ADR-023`) |
| **App** | 10. App and Extension App Artifacts | Một host ASP.NET Core gom nhiều FB + expose Public Object Model | `src/Apps/Nvm.App.Execution/` | `đang làm` | `Nvm.Host.All` đã chạy với `/health/live` + `/health/ready` (C03, C10). Chưa gom FB nào, chưa có Public Object Model |
| **Public Object Model** | 10 | **Chính là CQRS read side**: OData v4 endpoint trên read model Postgres | `src/Apps/*/PublicObjectModel/` | `chưa làm` | M4, kèm ADR-013 |
| **Extension App** | 11, 21. Extension App Development | App Mendix mở rộng UI + gọi command | `mendix/`, `src/Apps/Nvm.App.Compliance/` | `đang làm` | App `NvmShopFloor` đăng nhập qua Keycloak xong (C13, `8d4ccfa`). Chưa gọi command nào — ranh giới K10 chưa được đi qua lần nào |
| **UI Component** | 20. App Development Process | Mendix page/snippet dùng lại giữa nhiều app | `mendix/NvmShared/` | `đang làm` | Module `NvmShared` đã có: entity `NvmAccount`, 2 microflow. Chưa có snippet dùng lại |
| **Project Studio** (add-in Visual Studio) | 13. Project Studio Overview | `dotnet new` template + Roslyn analyzer ép quy ước FB | `tools/templates/`, `tools/analyzers/` | `đang làm` | **Analyzer đã có**: `NVM001` (cấm `DateTime.UtcNow`), `NVM002` (cấm `DateTime` trong contract), `NVM003` (ép `[EventVersion]`) — cả ba làm **build FAILED** (C15, C16). `tools/templates/` còn rỗng: `dotnet new nvm-fb` chỉ có nghĩa sau khi bố cục FB chốt ở M5 |
| **Solution Studio** (web app) | 14. Solution Studio Overview | CLI + manifest `solution.yaml` compose các App | `tools/solution-cli/` | `chưa làm` | M12 |
| **Package Versioning** | 15. Package Versioning | SemVer cho từng FB, compatibility matrix | `docs/package-versioning.md` | `chưa làm` | M12, kèm ADR-018 |
| **Manufacturing Solution** | 8. Manufacturing Solution Structure | Toàn bộ repo = một manufacturing solution | `solution.yaml` | `chưa làm` | `solution.yaml` chưa tồn tại |
| **Multiplant Management** | 7. Multiplant Management | `SiteId` là first-class trong mọi entity, event, query, và trong Mendix XPath | `docs/scope.md` §5.6 | `đang làm` | `SiteId` bắt buộc trên `IDomainEvent`; là **đoạn đầu routing key** nên consumer bind được `nvm.NV1.#` và không bao giờ *nhận* message của site khác; seed có NV1 + DE1. **Staged rollout được chứng minh qua handler**: NV1 chạy revision 3 trong khi DE1 còn ở 1, và event của NV1 không chứa path nào của DE1 (`ADR-024`). Chưa có query/DB nào để ép filter phía server (M6) |
| **Factory Model** | 3. System Architecture | Cây ISA-95 `Enterprise/Site/Area/Line/WorkCell/Equipment` | `src/FunctionalBlocks/FactoryModel/` | `đang làm` | Cây 6 bậc thật cho NV1 + DE1, `EquipmentPath` tra O(1). `IFactoryModelCatalog` giữ **ba revision** (`r1` 41 node · `r2` +4 kênh sạc · `r3` tháo `FORM-02`, thêm `STACK-04`); mỗi revision là **một tài liệu bất biến**, không sửa tại chỗ (`ADR-024`). Chuyển revision 2 → 3 chạy qua handler và event báo đúng phần **thêm** lẫn phần **bớt**. Mọi collection lộ ra ngoài là `ImmutableArray`, flat index là `FrozenDictionary` (`ADR-025`). Nguồn vẫn là **file seed**, chưa phải database (plan M1 §3.1), và **revision đang hiệu lực mất khi restart** — đó là lý do chưa `xong` |
| **Technology Stack** | 4. Technology Stack | .NET 10, SQL Server, RabbitMQ, OData — cố ý bám sát stack Opcenter thật | `docs/scope.md` §5.5 | `đang làm` | .NET 10 (ADR-019); RabbitMQ **thật sự được dùng** qua MassTransit 8 (ADR-004, ADR-021), không chỉ chạy trong compose. SQL Server vẫn mới chỉ có health check. **OData chưa có** (M4) |
| **Scalability / dev modes** | 5. Scalability and Development Modes | Chạy monolith (dev) hoặc tách process (prod) qua cùng `solution.yaml` | `docs/scope.md` §5.4 | `chưa làm` | Phụ thuộc `solution.yaml` |

---

## Tổng kết sau M1

| Trạng thái | Sau M0 | **Hiện tại (M1 chưa đóng)** |
|---|---|---|
| `xong` | 0 | **0** |
| `đang làm` | 6 | **16** |
| `chưa làm` | 16 | **6** |
| `khác scope` | 0 | 0 |

**Chưa ô nào `xong`, và con số 0 đó là con số đúng.** Hai ô gần nhất — **Bus-Centric Design** và
**Manufacturing Service Bus** — đã đạt **vế thứ nhất**: dựng được, chạy được, có số đo. Vế thứ hai của
chính file này (*"giải thích được vì sao Opcenter làm như vậy"*) thì chưa: D5 của M1 yêu cầu chủ repo
trả lời ba câu hỏi ở `M1-factory-model-bus.md` §C18 **thành lời, không mở tài liệu**, và việc đó chưa
xảy ra.

Hạ trạng thái không phải thất bại — ghi `xong` khi chưa đạt mới là, vì nó làm hỏng đúng thước đo mà
file này tồn tại để giữ.

Con số đó là **18/200 event mất** khi broker chết 30 giây (`benchmarks.md`, `ADR-022`). Nó nói hai
điều cùng lúc:

- Bus-centric là **điều kiện** để N15 khả thi — app không restart lần nào, `/health/live` xanh suốt,
  dây chuyền không dừng vì MES mất kết nối.
- Và bus **một mình không đủ**. 18 event biến mất vì không có transaction nào nối "ghi trạng thái"
  với "publish". Đó là bẫy dual-write, và là toàn bộ lý do outbox tồn tại ở M6.

Mười sáu ô `đang làm` không phải nửa vời — phần lớn đang chờ **một thứ duy nhất: database**.
`Facets/`, `Migrations/`, `TransactionBehavior`, event store, upcaster, schema riêng cho từng bounded
context, filter ép ở server cho multiplant — tất cả cùng đến ở **M5**.

**Khái niệm trung tâm còn lại**: Facet (`chưa làm`) và Public Object Model (`chưa làm`). Hai cái này
cộng với Functional Block đầy đủ mới là thứ phân biệt Opcenter với một app .NET có bus. Chúng nằm ở
M4–M6.

> [!note] Đừng đọc bảng này như thanh tiến độ
> `xong` ở đây khó hơn "code chạy". Nó đòi vế thứ hai — nói lại được **vì sao** — nên một khái niệm
> có thể ngồi ở `đang làm` rất lâu dù code đã đầy đủ. Đó là chủ đích: mục tiêu dự án là hiểu mô
> hình, không phải có thư mục đúng tên.
