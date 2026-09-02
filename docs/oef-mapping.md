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
| **Bus-Centric Design** | 1. Key Design Principles | RabbitMQ + MassTransit, mọi giao tiếp liên-FB đi qua bus | `src/Platform/Nvm.Bus/` | `xong` | Cả hai vế đã đóng ở M1/D5 ngày 2026-08-30: một publish → **2 consumer, 2 queue riêng**, và chủ repo giải thích được quan hệ N15/K12. Lab phá hoại đo **18/200 event mất** khi broker chết 30 s, app **0 restart**, `/health/live` xanh suốt — bus tách vòng đời các FB nhưng chưa thay thế outbox (`benchmarks.md`, `ADR-022`) |
| **Manufacturing Service Bus** | 1, 6. RabbitMQ Configuration | Topology exchange/queue, retry, DLQ, delayed message | `src/Platform/Nvm.Bus/Topology/` | `xong` | M1/D5 đã đóng cả vế chạy được và giải thích được. Exchange theo context + routing key `nvm.{site}.{context}.{event}.v{n}`; retry đo **5 lần / 245–480–920–1933 ms** có jitter; **1 message vào `_error`, queue chính còn 0**. **Delayed message cố ý chưa làm**: image không có plugin; saga M7 dự kiến dùng Quartz store và sẽ chốt bằng `ADR-015` |
| **SOA layers** | 1 | Horizontal (Platform) + vertical (Functional Block) | `src/Platform/` vs `src/FunctionalBlocks/` | `đang làm` | Platform có `Nvm.Contracts`, `Nvm.Kernel`, `Nvm.Bus`; FunctionalBlocks có `FactoryModel`. Ranh giới được **ép bằng NetArchTest** (C17). Chưa `xong` vì mới có **một** FB — quy tắc "FB không reference FB" chưa có gì để vi phạm |
| **Event-Driven Architecture** | 1 | CloudEvents envelope + domain event trên bus | `src/Platform/Nvm.Contracts/` | `đang làm` | Envelope §7.4 + `IDomainEvent` + `[EventVersion]` + golden file (C01–C03); thuộc tính CloudEvents đi ở transport header trên message thật (C12, `ADR-008`). Còn thiếu **event store** và **upcaster** — đó mới là phần chứng minh đọc được event 15 năm sau, và cả hai thuộc **M5** |
| **Domain-Driven Design / bounded context** | 1, 17. Domain within OEF | Mỗi Functional Block = một bounded context có schema DB riêng | `src/FunctionalBlocks/<Name>/` | `đang làm` | `FactoryModel` là bounded context đầu tiên, có từ vựng riêng và exchange riêng trên bus. **Chưa có schema DB riêng** — chưa có DB nào (M5) |
| **Functional Block** | 9. Functional Block Artifacts | Class library có `Entities/`, `Commands/`, `Handlers/`, `Facets/`, `Events/`, `Migrations/` | `src/FunctionalBlocks/FactoryModel/` | `đang làm` | FB đầu tiên có `Entities/`, `Commands/`, `Handlers/`, `Seeding/`, `Storage/`. Thiếu **`Facets/`** và **`Migrations/`** — hai thứ đó cần event store và DB, tức M5 |
| **Entity** | 9, 18–19 | Aggregate root / entity trong FB | `.../Entities/FactoryNode.cs` | `đang làm` | `FactoryNode`, `FactorySite`, `FactoryModelSnapshot` — bất biến, invariant ép bằng kiểu (`EquipmentPath` quyết định bậc). Chưa phải **aggregate root event-sourced** (M5) |
| **Facet** (mở rộng entity) | 18–19 | Bảng mở rộng + interface `IFacetOf<TEntity>` | `.../Facets/` | `chưa làm` | M5 |
| **Command / Extended Command** | 18–19 | `record XxxCommand : ICommand` | `.../Commands/` | `đang làm` | `ICommand<TResult>` + `ActivateFactoryModelRevisionCommand`, natural key đặt cạnh command (`KeyFor`). **Extended command** — FB khác thêm command vào FB này — chưa có gì để thử |
| **Command Handler** | 18–19 | `ICommandHandler<TCommand>` + pipeline behavior | `.../Handlers/` | `đang làm` | Dispatcher + **3/4 behavior**: Validation → Idempotency → Audit. Idempotency **giành chỗ trước** khi gọi handler (`Claim`/`Complete`/`Abandon`), nên thứ tự Validation → Idempotency là correctness **thật** chứ không phải preference. Bảo đảm hiện có là **process-local**: duplicate tuần tự và duplicate đồng thời trong cùng một process chỉ tạo **một** effect, và mọi nhánh lỗi `Abandon` để khoá mở lại. **Chưa đạt**: restart (store mất sạch), nhiều instance (hai process không thấy nhau), và atomicity giữa idempotency record với business effect. **K7 vì thế CHƯA ĐÓNG** — cần cả **`TransactionBehavior`** lẫn một store bền vững, và cả hai cần database: **M4** chịu trách nhiệm (`ADR-023`, lịch cập nhật 2026-08-30 — M4 tạo effect bền vững nên điều kiện kéo lên đã xảy ra) |
| **App** | 10. App and Extension App Artifacts | Một host ASP.NET Core gom nhiều FB + expose Public Object Model | `src/Apps/Nvm.Host.All/` | `đang làm` | `Nvm.Host.All` đã gom FB đầu tiên `FactoryModel`, command pipeline và `Nvm.Time` production calendar; `/health/live` + `/health/ready` đã chạy. Chưa có Public Object Model và chưa có FB thứ hai để chứng minh composition nhiều FB |
| **Public Object Model** | 10 | **Chính là CQRS read side**: OData v4 endpoint trên read model Postgres | `src/Apps/*/PublicObjectModel/` | `chưa làm` | M4, kèm ADR-013 |
| **Extension App** | 11, 21. Extension App Development | App Mendix mở rộng UI + gọi command | `mendix/`, `src/Apps/Nvm.App.Compliance/` | `đang làm` | App `NvmShopFloor` đăng nhập qua Keycloak xong (C13, `8d4ccfa`). Chưa gọi command nào — ranh giới K10 chưa được đi qua lần nào |
| **UI Component** | 20. App Development Process | Mendix page/snippet dùng lại giữa nhiều app | `mendix/NvmShared/` | `đang làm` | Module `NvmShared` đã có: entity `NvmAccount`, 2 microflow. Chưa có snippet dùng lại |
| **Project Studio** (add-in Visual Studio) | 13. Project Studio Overview | `dotnet new` template + Roslyn analyzer ép quy ước FB | `tools/templates/`, `tools/analyzers/` | `đang làm` | **Analyzer đã có**: `NVM001` (cấm `DateTime.UtcNow`), `NVM002` (cấm `DateTime` trong contract), `NVM003` (ép `[EventVersion]`) — cả ba làm **build FAILED** (C15, C16). `tools/templates/` còn rỗng: `dotnet new nvm-fb` chỉ có nghĩa sau khi bố cục FB chốt ở M5 |
| **Solution Studio** (web app) | 14. Solution Studio Overview | CLI + manifest `solution.yaml` compose các App | `tools/solution-cli/` | `chưa làm` | M12 |
| **Package Versioning** | 15. Package Versioning | SemVer cho từng FB, compatibility matrix | `docs/package-versioning.md` | `chưa làm` | M12, kèm ADR-018 |
| **Manufacturing Solution** | 8. Manufacturing Solution Structure | Toàn bộ repo = một manufacturing solution | `solution.yaml` | `chưa làm` | `solution.yaml` chưa tồn tại |
| **Multiplant Management** | 7. Multiplant Management | `SiteId` là first-class trong mọi entity, event, query, và trong Mendix XPath | `docs/scope.md` §5.6 | `đang làm` | `SiteId` bắt buộc trên event và routing key; seed có NV1 + DE1, staged rollout đã được chứng minh (`ADR-024`). Từ M3, telemetry và cả rollup mức kênh/máy đi qua schema `ts_scoped` với filter **ép ở server** theo role; role Grafana đọc được site được cấp nhưng bị từ chối khi đọc thẳng schema `ts`. Write model, POM và Mendix XPath vẫn chưa có nên khái niệm multiplant chưa `xong` |
| **Factory Model** | 3. System Architecture | Cây ISA-95 `Enterprise/Site/Area/Line/WorkCell/Equipment` | `src/FunctionalBlocks/FactoryModel/` | `đang làm` | Cây 6 bậc thật cho NV1 + DE1, `EquipmentPath` tra O(1). `IFactoryModelCatalog` giữ **ba revision** (`r1` 41 node · `r2` +4 kênh sạc · `r3` tháo `FORM-02`, thêm `STACK-04`); mỗi revision là **một tài liệu bất biến**, không sửa tại chỗ (`ADR-024`). Chuyển revision 2 → 3 chạy qua handler và event báo đúng phần **thêm** lẫn phần **bớt**. Mọi collection lộ ra ngoài là `ImmutableArray`, flat index là `FrozenDictionary` (`ADR-025`). Nguồn vẫn là **file seed**, chưa phải database (plan M1 §3.1), và **revision đang hiệu lực mất khi restart** — đó là lý do chưa `xong` |
| **Technology Stack** | 4. Technology Stack | .NET 10, SQL Server, RabbitMQ, OData — cố ý bám sát stack Opcenter thật | `docs/scope.md` §5.5 | `đang làm` | .NET 10 (ADR-019); RabbitMQ **thật sự được dùng** qua MassTransit 8 (ADR-004, ADR-021), không chỉ chạy trong compose. SQL Server vẫn mới chỉ có health check. **OData chưa có** (M4) |
| **Scalability / dev modes** | 5. Scalability and Development Modes | Chạy monolith (dev) hoặc tách process (prod) qua cùng `solution.yaml` | `docs/scope.md` §5.4 | `chưa làm` | Phụ thuộc `solution.yaml` |
| **Edge connector** | 8. Equipment Integration | Gateway MQTT/Sparkplug B đứng ở `dmz-net`, store-and-forward xuống đĩa | `src/Workers/Nvm.EdgeGateway/` | `đang làm` | Subscribe EMQX, decode Sparkplug B, đóng dấu `gateway_timestamp`, đệm append-only có CRC + cursor fsync (`ADR-028`). Rate limit + `Retry-After` khi xả (`ADR-029`). Theo dõi `NBIRTH`/`NDEATH`/`bdSeq`, `seq` nhảy cóc thì xin **rebirth**. D3 cuối: backend tắt **141 s**, backlog **7.288**, drain **37 s** trên ngân sách strict < 180, **16.288 = 16.288**, `abandonedMeasurements = 0`, restart 0. Chưa `xong` vì teach-back M2 còn mở |
| **Data Collection** | 8. Equipment Integration | Ingestion nhận batch protobuf qua HTTP, ghi TimescaleDB, và nhận cả CSV file drop | `src/Workers/Nvm.Ingestion/` | `đang làm` | Điểm bắc cầu duy nhất `dmz-net` → `it-net` (K11, K12). MQTT và CSV đi cùng cửa dedup; M2 chứng minh exact **2.361.174** message trên 1.000 kênh. N1/N2 chưa đạt (**3.933,2 msg/s**, p95 **99,5 s**) và được chuyển nghiệm thu sang M9/M13 theo `ADR-031`, không được mô tả là đã đạt. Phần kỹ thuật M3 có historian, site-scoped rollup phân cấp, archive, D5 raw↔parent↔child và tạo chunk trước claim; hai retention job cố ý tắt. Teach-back M3 chưa thực hiện, nên milestone và trạng thái này đều chưa `xong` |
| **Idempotency ở tầng thiết bị** | 8, 18–19 | `ingest.processed_message` — khoá UUIDv5 toàn cục, claim và effect cùng transaction | `src/Workers/Nvm.Ingestion/Persistence/` | `xong` | `INSERT ... ON CONFLICT DO NOTHING` cùng transaction với insert telemetry (`ADR-030`). Đây là **tầng thứ nhất** của hai tầng: tầng command handler vẫn process-local tới **M4** (`ADR-023`: claim + bản ghi nghiệp vụ + outcome trong cùng một transaction, có mặt trong hoặc trước commit đầu tiên của M4 có write). Hai lab đo đúng hai chiều hỏng: bỏ dedup → **+24,5 % row thừa**; bỏ `device_timestamp` khỏi khoá → **99,93 % row bị nuốt**. Cả hai đều **không ném lỗi nào** |

---

## Tổng kết hiện tại — sau implementation M3

| Trạng thái | Sau M0 | **Hiện tại** |
|---|---|---|
| `xong` | 0 | **3** |
| `đang làm` | 6 | **16** |
| `chưa làm` | 16 | **6** |
| `khác scope` | 0 | 0 |

Ba ô `xong` là **Bus-Centric Design**, **Manufacturing Service Bus** và **Idempotency ở tầng thiết bị**.
Hai ô bus đã đóng cả vế kỹ thuật lẫn teach-back ở M1; idempotency tầng thiết bị có transaction và hai
lab phá hoại đo được. M2 và M3 vẫn chưa đóng vì teach-back chưa hoàn tất. **Data Collection** giữ
`đang làm`, ghi đúng khoảng trống hiểu biết thay vì đồng nhất bằng chứng kỹ thuật với kết quả học.

Con số đó là **18/200 event mất** khi broker chết 30 giây (`benchmarks.md`, `ADR-022`). Nó nói hai
điều cùng lúc:

- Bus-centric là **điều kiện** để N15 khả thi — app không restart lần nào, `/health/live` xanh suốt,
  dây chuyền không dừng vì MES mất kết nối.
- Và bus **một mình không đủ**. 18 event biến mất vì không có transaction nào nối "ghi trạng thái"
  với "publish". Đó là bẫy dual-write, và là toàn bộ lý do outbox tồn tại ở M6.

Mười sáu ô `đang làm` không đồng nghĩa cùng một mức trưởng thành. Telemetry đã có database và filter
multiplant ép ở server; write model nghiệp vụ vẫn chưa có event store, upcaster, schema riêng cho
từng bounded context hay idempotency bền vững. Các phần đó tới ở M4–M5, không nên dùng tiến độ M3 để
suy ra chúng đã tồn tại.

**Khái niệm trung tâm còn lại**: Facet (`chưa làm`) và Public Object Model (`chưa làm`). Hai cái này
cộng với Functional Block đầy đủ mới là thứ phân biệt Opcenter với một app .NET có bus. Chúng nằm ở
M4–M6.

> [!note] Đừng đọc bảng này như thanh tiến độ
> `xong` ở đây khó hơn "code chạy". Nó đòi vế thứ hai — nói lại được **vì sao** — nên một khái niệm
> có thể ngồi ở `đang làm` rất lâu dù code đã đầy đủ. Đó là chủ đích: mục tiêu dự án là hiểu mô
> hình, không phải có thư mục đúng tên.
