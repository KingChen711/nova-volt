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
| **Bus-Centric Design** | 1. Key Design Principles | RabbitMQ + MassTransit, mọi giao tiếp liên-FB đi qua bus | `src/Platform/Nvm.Bus/` | `chưa làm` | Container RabbitMQ đã chạy từ C08, nhưng project `Nvm.Bus` chưa tồn tại |
| **Manufacturing Service Bus** | 1, 6. RabbitMQ Configuration | Topology exchange/queue, retry, DLQ, delayed message | `src/Platform/Nvm.Bus/Topology/` | `chưa làm` | M1 |
| **SOA layers** | 1 | Horizontal (Platform) + vertical (Functional Block) | `src/Platform/` vs `src/FunctionalBlocks/` | `đang làm` | Cây thư mục đã tách đúng; `Platform` mới có `Nvm.Kernel`, `FunctionalBlocks` còn rỗng |
| **Event-Driven Architecture** | 1 | CloudEvents envelope + domain event trên bus | `src/Platform/Nvm.Contracts/` | `chưa làm` | M6, kèm ADR-008 |
| **Domain-Driven Design / bounded context** | 1, 17. Domain within OEF | Mỗi Functional Block = một bounded context có schema DB riêng | `src/FunctionalBlocks/<Name>/` | `chưa làm` | M5 |
| **Functional Block** | 9. Functional Block Artifacts | Class library có `Entities/`, `Commands/`, `Handlers/`, `Facets/`, `Events/`, `Migrations/` | `src/FunctionalBlocks/Traceability/` | `chưa làm` | M5 — đây là khái niệm trung tâm của OEF |
| **Entity** | 9, 18–19 | Aggregate root / entity trong FB | `.../Entities/ProductionUnit.cs` | `chưa làm` | M5 |
| **Facet** (mở rộng entity) | 18–19 | Bảng mở rộng + interface `IFacetOf<TEntity>` | `.../Facets/` | `chưa làm` | M5 |
| **Command / Extended Command** | 18–19 | `record XxxCommand : ICommand` | `.../Commands/` | `chưa làm` | M5 |
| **Command Handler** | 18–19 | `ICommandHandler<TCommand>` + pipeline behavior | `.../Handlers/` | `chưa làm` | M5 |
| **App** | 10. App and Extension App Artifacts | Một host ASP.NET Core gom nhiều FB + expose Public Object Model | `src/Apps/Nvm.App.Execution/` | `đang làm` | `Nvm.Host.All` đã chạy với `/health/live` + `/health/ready` (C03, C10). Chưa gom FB nào, chưa có Public Object Model |
| **Public Object Model** | 10 | **Chính là CQRS read side**: OData v4 endpoint trên read model Postgres | `src/Apps/*/PublicObjectModel/` | `chưa làm` | M4, kèm ADR-013 |
| **Extension App** | 11, 21. Extension App Development | App Mendix mở rộng UI + gọi command | `mendix/`, `src/Apps/Nvm.App.Compliance/` | `đang làm` | App `NvmShopFloor` đăng nhập qua Keycloak xong (C13, `8d4ccfa`). Chưa gọi command nào — ranh giới K10 chưa được đi qua lần nào |
| **UI Component** | 20. App Development Process | Mendix page/snippet dùng lại giữa nhiều app | `mendix/NvmShared/` | `đang làm` | Module `NvmShared` đã có: entity `NvmAccount`, 2 microflow. Chưa có snippet dùng lại |
| **Project Studio** (add-in Visual Studio) | 13. Project Studio Overview | `dotnet new` template + Roslyn analyzer ép quy ước FB | `tools/templates/`, `tools/analyzers/` | `chưa làm` | Thư mục rỗng. Chỉ có nghĩa sau khi FB đầu tiên xong (M5) |
| **Solution Studio** (web app) | 14. Solution Studio Overview | CLI + manifest `solution.yaml` compose các App | `tools/solution-cli/` | `chưa làm` | M12 |
| **Package Versioning** | 15. Package Versioning | SemVer cho từng FB, compatibility matrix | `docs/package-versioning.md` | `chưa làm` | M12, kèm ADR-018 |
| **Manufacturing Solution** | 8. Manufacturing Solution Structure | Toàn bộ repo = một manufacturing solution | `solution.yaml` | `chưa làm` | `solution.yaml` chưa tồn tại |
| **Multiplant Management** | 7. Multiplant Management | `SiteId` là first-class trong mọi entity, event, query, và trong Mendix XPath | `docs/scope.md` §5.6 | `đang làm` | `site_id` đã đi hết đường Keycloak → token → Mendix `NvmAccount.SiteId` (C13). Phía .NET chưa có entity nào mang `SiteId` |
| **Factory Model** | 3. System Architecture | Cây ISA-95 `Enterprise/Site/Area/Line/WorkCell/Equipment` | `src/FunctionalBlocks/FactoryModel/` | `chưa làm` | M1 |
| **Technology Stack** | 4. Technology Stack | .NET 10, SQL Server, RabbitMQ, OData — cố ý bám sát stack Opcenter thật | `docs/scope.md` §5.5 | `đang làm` | .NET 10 (ADR-019), SQL Server + RabbitMQ chạy trong compose. OData chưa có |
| **Scalability / dev modes** | 5. Scalability and Development Modes | Chạy monolith (dev) hoặc tách process (prod) qua cùng `solution.yaml` | `docs/scope.md` §5.4 | `chưa làm` | Phụ thuộc `solution.yaml` |

---

## Tổng kết sau M0

| Trạng thái | Số |
|---|---|
| `xong` | **0** |
| `đang làm` | 6 |
| `chưa làm` | 16 |
| `khác scope` | 0 |

Không có ô `xong` nào, và đó là kết quả đúng: M0 dựng khung chạy được chứ không dựng khái niệm
OEF nào. Sáu ô `đang làm` đều là hạ tầng — host, cây thư mục, stack, và đường đăng nhập từ Mendix.

**Khái niệm trung tâm chưa chạm tới**: Functional Block, Facet, Command Handler, Public Object
Model. Bốn cái này mới là thứ phân biệt Opcenter với một app .NET bình thường, và chúng tập trung
ở M4–M6. Trước đó, bảng này sẽ trông không nhúc nhích mấy — đừng vì thế mà tưởng dự án đứng yên.
