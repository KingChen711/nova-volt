---
title: "M1 — Factory Model & Manufacturing Service Bus"
milestone: M1
duration: "1,5 tuần (18 giờ ước lượng)"
status: planned
created: 2026-08-26
depends_on: [M0]
unlocks: [M2]
---

# M1 — Factory Model & Manufacturing Service Bus

> **Mục tiêu**: dựng "Bus-Centric Design" của OEF bằng tay, và cây ISA-95 làm nền cho mọi thứ sau này.
>
> Đọc trước: [`AGENTS.md`](../../AGENTS.md) §4 (K1, K2, K6, K7, K8, K9, K12) · [`scope.md`](../scope.md) §5.2 (ánh xạ OEF), §7.2 (idempotency), §7.4 (CloudEvents), §9/M1 · [`M0-bootstrap.md`](M0-bootstrap.md) §C05, §C08, §C10

---

## 1. Definition of Done

Milestone chỉ được đóng khi **cả 5** mệnh đề đúng, có bằng chứng chạy được:

| # | Tiêu chí | Cách chứng minh |
|---|---|---|
| ★ D1 | Publish **1** event từ `Nvm.Host.All` → **2** consumer ở process khác nhận độc lập, **mỗi consumer một queue riêng** | `make bus-fanout` in ra 2 dòng nhận · `rabbitmq-diagnostics list_queues` cho thấy 2 queue có `messages` đếm riêng |
| D2 | Consumer ném exception **5 lần** → message vào `_error` queue, **không mất** | `make bus-dlq`: log có đúng 5 lần thử · `<queue>_error` có 1 message · queue chính rỗng |
| D3 | Analyzer làm **build FAILED** khi cố tình viết `DateTime.UtcNow` | Output `dotnet build` có `error NVM001` |
| D4 | Tắt RabbitMQ giữa lúc publish → app **không crash**, `/health/live` vẫn `Healthy`, publish lỗi **bị bắt và đếm**, và **số event mất được ghi thành con số** | Lab phá hoại §5.C13.3 → số vào `benchmarks.md` + `ADR-022`. Mệnh đề này **đã được phát biểu lại** so với `scope.md` — lý do ở §3/Q3 |
| D5 | 6 dòng OEF của M1 trong `docs/oef-mapping.md` có trạng thái **đúng**, trong đó *Bus-Centric Design* và *Manufacturing Service Bus* đạt `xong` | Bảng cập nhật + trả lời được câu hỏi kiểm tra ở §5.C18 |

**Sản phẩm phụ bắt buộc**

- `make test` vẫn xanh, và số test tăng thật (M0 kết thúc ở 22).
- `tests/Architecture` có ≥ 5 rule chạy được (K8, K9, K2 được ép bằng máy chứ không bằng trí nhớ).
- **5** ADR mới: `ADR-004`, `ADR-008`, `ADR-010`, `ADR-021`, `ADR-022`. Mỗi ADR viết **trong chính commit ra quyết định**, không dồn về cuối.
- `docs/event-catalog.md` tồn tại với các event M1 thật sự phát ra.
- `docs/glossary.md` phủ **mọi** thuật ngữ nghiệp vụ đã dùng trong M1 — kiểm bằng cách đọc lại báo cáo của 19 commit và tìm từ chưa có mục (`AGENTS.md` §5.8.2).

**Không** thuộc M1: event store, outbox, projection, Functional Block đầy đủ (`Entities/`+`Migrations/`), OData/Public Object Model, EF Core, Testcontainers. M1 dựng **đường ống** và **quy ước**, không dựng nghiệp vụ.

> [!important] Ranh giới dễ trôi nhất của M1
> M1 rất dễ phình thành M5. Dấu hiệu: bắt đầu viết migration, bắt đầu nghĩ về optimistic concurrency, bắt đầu tạo `ProductionUnit`. Dừng lại. Câu hỏi kiểm tra: *thứ tôi đang viết có cần đến database nào không?* Nếu có, gần như chắc chắn nó thuộc M5 hoặc M6.

---

## 2. Phát hiện trước khi bắt đầu — bốn điều chỉnh so với `scope.md`

Kiểm tra ngày 2026-08-26. Xử lý theo [`AGENTS.md` §2.3](../../AGENTS.md).

### 2.1 MassTransit v9 đã chuyển sang license thương mại — pin cứng v8

| | |
|---|---|
| **Thực tế** | Bản stable mới nhất trên NuGet là `9.2.0`. Nuspec của nó ghi `licenseUrl = https://massient.com/license`, `projectUrl = https://massient.com/`. Nuspec của `8.5.10` ghi `<license type="expression">Apache-2.0</license>`. |
| **Vấn đề** | Điều khoản §3 của license đó nói thẳng: *MassTransit v8 trở về trước vẫn free/open source nhưng **không được hỗ trợ** theo thoả thuận này; thoả thuận chỉ áp dụng cho v9 trở lên.* Nghĩa là dùng v9 trong một dự án thương mại là phải trả tiền theo product line. |
| **Quyết định** | Pin **`MassTransit 8.5.10`** + **`MassTransit.RabbitMQ 8.5.10`**. |
| **Đánh đổi** | v8 vẫn hỗ trợ `net10.0` (đã kiểm nuspec: có nhóm `targetFramework="net10.0"`, kéo `Microsoft.Extensions.* 10.0.0` — không lệch version với host hiện tại) và kéo `RabbitMQ.Client 7.2.1`, tương thích RabbitMQ 4.x đang chạy trong compose. Cái mất là: không có bản vá mới cho v8, và mọi tài liệu mới trên masstransit.io sẽ dần nói về v9. |
| **Việc phải làm** | `ADR-021` ở C09. Ghi kèm chính xác hai dòng nuspec làm bằng chứng — đây là loại quyết định phải giải thích được với tech lead, đúng như ghi chú về FluentAssertions ở `scope.md` §10.1. |

> Cùng một bài học lặp lại lần thứ hai trong dự án này: **kiểm license của thư viện trước khi pin version, không chỉ kiểm nó có chạy không.** Lần đầu là FluentAssertions v8 (M0/C04).

### 2.2 SDK 10.0.300 ship Roslyn 5.6.0 — analyzer không được pin cao hơn

| | |
|---|---|
| **Thực tế** | `C:\Program Files\dotnet\sdk\10.0.300\Roslyn\bincore\Microsoft.CodeAnalysis.CSharp.dll` có `ProductVersion = 5.6.0-2.26230.102`. Trên NuGet, bản stable mới nhất của `Microsoft.CodeAnalysis.CSharp` là `5.9.0`. |
| **Vấn đề** | Analyzer được **compiler nạp**, không phải app nạp. Build analyzer dựa trên Roslyn 5.9.0 rồi nạp vào compiler 5.6.0 sẽ ném `AD0001` / `CS8032 An instance of analyzer cannot be created` — và triệu chứng bên ngoài là *analyzer im lặng không chạy*, không phải một lỗi rõ ràng. |
| **Quyết định** | Pin `Microsoft.CodeAnalysis.CSharp` = **`5.6.0`**, `PrivateAssets="all"`. Quy tắc chung: version của gói này phải **≤** Roslyn trong SDK thấp nhất mà repo hỗ trợ. |
| **Việc phải làm** | Ghi thẳng quy tắc đó thành comment trong `Directory.Packages.props` ở C15, kèm lệnh đo lại. `global.json` đã pin SDK nên hai con số này gắn với nhau. |

### 2.3 Ba ADR bị xếp vào M6 — quyết định thật ra ở M1

| | |
|---|---|
| **Thực tế** | `docs/adr/README.md` xếp `ADR-004` (RabbitMQ vs Kafka), `ADR-008` (CloudEvents envelope) và `ADR-010` (idempotency key = UUIDv5) vào M6. `docs/oef-mapping.md` cũng ghi *Event-Driven Architecture → `Nvm.Contracts` → M6*. Nhưng `scope.md` §9/M1 liệt kê `Nvm.Contracts`, bus topology **và** `IdempotencyKey` là việc của **M1**. |
| **Vấn đề** | Không phải mâu thuẫn thiết kế, chỉ là các file viết ở những thời điểm khác nhau. Nhưng để nguyên thì tới M6 sẽ có người đi tìm một quyết định đã ra từ 4 tháng trước — và ADR viết muộn là ADR mất phần *Alternatives*, thứ đáng giá nhất trong đó. |
| **Quyết định** | Viết cả ba ở **M1**, tại đúng commit ra quyết định: `ADR-010` ở **C04** (lúc viết `IdempotencyKey`), `ADR-004` ở **C09** (lúc pin thư viện bus), `ADR-008` ở **C12** (lúc chốt hình dạng message trên dây). Cập nhật cột *Ra ở* trong `docs/adr/README.md` và cột *Ghi chú* trong `oef-mapping.md` ngay tại C18. |
| **Ghi chú** | `ADR-010` là ví dụ rõ nhất cho quy tắc này. Quyết định "v5 chứ không v7" chỉ có sức thuyết phục khi đi kèm output đã chạy (C04.1) — viết lại sau 4 tháng thì output đã mất, và ADR biến thành một câu khẳng định không có gì đỡ. |
| **Ảnh hưởng DoD** | D5 phát biểu lại: `scope.md` viết *"`oef-mapping.md` có 6 dòng đầu tiên"*, nhưng M0/C15 đã tạo **cả 22 dòng**. Tiêu chí thật của M1 không phải *có dòng* mà là *trạng thái đúng*, và ≥ 2 dòng đạt `xong` theo đúng định nghĩa hai vế của file đó (dựng được **và** giải thích được). |

### 2.4 N13 đã được đo ở M0 — M1 kiểm regression thay vì đo lại

`scope.md` §4 gán N13 (*toàn hệ thống healthy < 5 phút*) cho M1. M0/C11 đã đo: **48 / 36 / 42 s** so với ngưỡng 300 s (`benchmarks.md`).

M1 **không thêm container nào** vào compose — RabbitMQ đã chạy từ M0/C08. Nên N13 không cần đo lại. Thứ M1 phải kiểm là **regression**: thêm bus health check vào `/health/ready` (C14) làm số check tăng từ 5 lên 6, và MassTransit đăng ký health check của nó **mặc định**. Rủi ro cụ thể là app không khởi động nổi hoặc `ready` không bao giờ xanh khi broker tắt — đúng thứ K12/N15 cấm. Xem C14.

---

## 3. Quyết định đã chốt

Xác nhận ngày 2026-08-26. Chủ repo uỷ quyền cho agent quyết cả ba, với thứ tự ưu tiên: **học nghiệp vụ > sát production > tiết kiệm giờ công**.

| # | Vấn đề | Quyết định | Hệ quả lên plan |
|---|---|---|---|
| Q1 | FactoryModel lưu ở đâu | **Seed file**, chưa database | C07 nạp `deploy/seed/factory-model.json`. Persistence vào M5 cùng bố cục FB đầy đủ |
| Q2 | CloudEvents nằm ở đâu trên dây | **Attribute ở transport header**, body giữ envelope MassTransit | C12 viết filter + `ADR-008`. Envelope đầy đủ §7.4 là contract của event store (M6), không phải của dây |
| Q3 | Lab phá hoại M1 | **Phát biểu lại DoD**: đếm số event mất thay vì đòi bằng 0 | D4 ở §1 đã sửa. `ADR-022` bắt buộc. `scope.md` §9/M1 và §9/M6 **đã sửa** 2026-08-26 |

### 3.1 Q1 — Vì sao seed file, khi Opcenter thật để factory model trong database

Cây ISA-95 **là** master data trong DB ở mọi MES thật, kể cả Opcenter. Nhưng nó **vào** DB bằng đường nào mới là câu hỏi đáng học: trong nhà máy thật, factory model hiếm khi được gõ tay vào MES. Nó đến từ **bản vẽ kỹ thuật và hệ thống engineering**, dưới dạng một file import — đúng thứ `scope.md` §9/M11 gọi là *ERP B2MML & Master Data Reconciliation*.

Nên `factory-model.json` ở M1 **không phải** phiên bản rút gọn của "cái thật". Nó là **bước đầu tiên của đường import thật**, và M5 chỉ thêm cho nó một chỗ ở trong SQL Server.

Hai lý do phụ, đều thực dụng:

- Chủ repo đã quen EF Core và migration → giá trị học của việc viết chúng ở M1 gần bằng 0, trong khi giá trị học của cây ISA-95 và `equipment_path` thì rất cao. Đặt công sức đúng chỗ.
- M5 mới chốt bố cục Functional Block thật (`Entities/` `Facets/` `Migrations/`). Viết migration ở M1 gần như chắc chắn phải viết lại ở M5 — và một schema bị viết lại là một schema mà bạn sẽ không tin nữa.

**Đánh đổi phải chấp nhận**: hết M1 vẫn chưa có bảng nào mang cột `SiteId NOT NULL`, nên K3 chưa được ép ở tầng DB. Bù lại bằng rule A5 của architecture test (C17) — ép ở tầng event trước, ép ở tầng bảng sau.

### 3.2 Q2 — Vì sao CloudEvents ở header, không phải ở body

Điều làm quyết định này dễ: theo K10 và `scope.md` §7.5, **Mendix không bao giờ đọc bus**. Nó đi qua Public Object Model (OData) và Command API. Nên ở M1 **mọi** consumer trên bus đều là .NET, và một body CloudEvents thuần không mua được gì trong khi làm mất định tuyến theo message type, mất metadata replay của `_error` queue.

Thứ đáng học ở đây — và là lý do `ADR-008` xứng đáng tồn tại — là phân biệt **hai khái niệm hay bị nhập làm một**:

| | Là gì | Ai sở hữu | Sống ở đâu |
|---|---|---|---|
| **Envelope của framework** | MassTransit gói message để nó tự định tuyến, retry, fault | MassTransit | chỉ trên dây, giữa hai process .NET |
| **Envelope CloudEvents §7.4** | Contract nghiệp vụ: cái gì xảy ra, ở site nào, do đâu gây ra, theo schema nào | `Nvm.Contracts` | **event store** (M6), file DPP (M12), mọi lần publish ra ngoài tổ chức |

Nhập hai cái làm một là lỗi thiết kế hay gặp: hoặc bạn để framework quyết định contract nghiệp vụ (đổi thư viện là vỡ hợp đồng), hoặc bạn ép framework mang nguyên contract nghiệp vụ và mất hết tính năng của nó.

Cách đã chọn giữ cả hai, và **`ce_id == IdempotencyKey`** là mối nối giữa chúng.

> [!note] Không có binding CloudEvents chính thức cho AMQP 0-9-1
> CloudEvents có binding cho HTTP (header tiền tố `ce-`), Kafka (`ce_`), AMQP **1.0** (application-property tiền tố `cloudEvents:`), MQTT, NATS. RabbitMQ ở đây chạy **AMQP 0-9-1** — không nằm trong danh sách đó.
>
> Nghĩa là dù chọn phương án nào thì cũng đang **tự đặt quy ước**. Ta chọn tiền tố **`ce_`** theo kiểu Kafka binding (gần nhất về hình dạng), và `ADR-008` phải ghi rõ đây là quy ước nội bộ chứ không phải chuẩn — để năm sau không ai đi tìm spec cho nó.

### 3.3 Q3 — Vì sao đổi DoD của lab phá hoại

`scope.md` §9/M1 viết: *"tắt RabbitMQ giữa lúc publish → producer phải buffer/retry, **không mất event**, không crash"*.

Theo [`AGENTS.md` §3.2](../../AGENTS.md):

> **PHÁT HIỆN**: Ở M1 chưa có transactional outbox — nó là nội dung của M6 (`scope.md` §5.5). Không có outbox thì *"không mất event"* là mệnh đề **không thể đạt**: khi broker tắt, `Publish` thất bại, và event chỉ tồn tại trong bộ nhớ process. Retry in-memory chỉ cứu được các lần thử trong vòng đời của process đó.
>
> **BẰNG CHỨNG**: `scope.md` §5.5 khối *"Bẫy dual-write"* mô tả đúng tình huống này, kết luận outbox là lời giải, và xếp nó vào M6. Chưa có mã nào trong repo ghi event xuống đâu trước khi publish.
>
> **ĐỀ XUẤT**: Phát biểu lại D4 thành ba vế **đo được**: (1) app không crash, `/health/live` vẫn `Healthy`; (2) publish thất bại được **bắt và đếm**, không nuốt lặng; (3) **số event mất được ghi thành con số** vào `benchmarks.md` và `ADR-022`.
>
> **ẢNH HƯỞNG**: M1 giữ nguyên thời lượng. M6 nhận thêm một tiêu chí: *chạy lại đúng kịch bản này với outbox, số mất phải về 0.* — **đã cập nhật vào `scope.md` §9/M1 và §9/M6 ngày 2026-08-26**, cùng lượt với plan này.

Giữ nguyên câu chữ của scope thì phải kéo outbox từ M6 về M1 (+4–6 h) và làm M1 phụ thuộc vào một schema mà M5 chưa chốt. Đã loại.

Nhưng lý do chính để đổi **không phải** tiết kiệm giờ. Nó là giá trị học:

> Đọc một đoạn về "bẫy dual-write" thì hiểu được ý. **Nhìn thấy 37 event biến mất** thì không quên được. Con số đo ở C13.3 chính là câu trả lời cho câu hỏi mà mọi tech lead sẽ hỏi ở M6: *"outbox thêm một bảng, một worker và một transaction — đổi lại được gì?"*

Đó là lý do lab này **không được** bỏ qua và **không được** làm tròn về 0.

---

## 4. Tổng quan 19 commit

| # | Commit message | Giai đoạn | Ước lượng | Phụ thuộc |
|---|---|---|---|---|
| C01 | `feat(contracts): add domain event marker and event version attribute` | A — Contract & Kernel | 45' | — |
| C02 | `feat(contracts): add cloudevents envelope and event type naming` | A | 60' | C01 |
| C03 | `test(contracts): add golden file and source-generated json context` | A | 45' | C02 |
| C04 | `feat(kernel): add icommand, icommandhandler and dispatcher` | A | 60' | — |
| C05 | `feat(kernel): add validation, idempotency and audit behaviors` | A | 60' | C04 |
| C06 | `feat(factory-model): add isa-95 node model and invariants` | B — Factory Model | 60' | C01 |
| C07 | `feat(factory-model): add seed for NV1 and DE1 with equipment path index` | B | 75' | C06 |
| C08 | `feat(factory-model): add command to activate a model revision` | B | 45' | C05, C07 |
| C09 | `build(bus): pin masstransit 8 and add nvm.bus project` | C — Service Bus | 45' | C02 |
| C10 | `feat(bus): add exchange, routing key and queue topology` | C | 75' | C09 |
| C11 | `feat(bus): add retry, redelivery and dead letter policy` | C | 60' | C10 |
| C12 | `feat(bus): carry cloudevents attributes as transport headers` | C | 60' | C10 |
| C13 | `feat(bus): add bus probe worker with two independent consumers` | C | 75' | C08, C11, C12 |
| C14 | `feat(host): add bus health check without breaking startup` | C | 45' | C13 |
| C15 | `feat(analyzers): add NVM001 forbidding DateTime.UtcNow` | D — Ép quy ước | 75' | C02 |
| C16 | `feat(analyzers): add NVM002 and NVM003 for event contracts` | D | 60' | C15 |
| C17 | `test(architecture): add netarchtest rules for platform boundaries` | D | 45' | C13 |
| C18 | `docs: add event catalog and update oef mapping` | E — Đóng M1 | 60' | tất cả |
| C19 | `docs: close m1 with benchmarks and checklist` | E | 30' | tất cả |

**Tổng: 1080 phút = 18 giờ**, khớp đúng tổng cột *Ước lượng* — không làm tròn xuống cho dễ nhìn. Với 10–12 giờ/tuần thì M1 rơi vào **1,5–1,8 tuần**; nếu tới ngày thứ 7 mà chưa qua C11 thì cắt theo R-M1-8.

Giai đoạn A và D gần như không cần hạ tầng — làm được cả lúc Docker tắt. Giai đoạn C bắt buộc `make up`.

> [!important] Nhắc lại từ AGENTS.md §1.1
> Agent **không tự commit**. Xong mỗi C, dừng lại, báo cáo, bạn tự đọc `git diff` rồi commit. Commit message ở cột trên là **đề xuất**.

> [!important] Nhắc lại từ AGENTS.md §5.8 — áp dụng từ C03 trở đi
> Mỗi commit **mở đầu** bằng khối **Nghiệp vụ** (khái niệm mới · vì sao có mặt · nếu làm sai thì hỏng gì trên dây chuyền), **rồi mới** code. Thuật ngữ chưa có trong [`docs/glossary.md`](../glossary.md) thì thêm vào trong chính commit đó.
>
> C01 và C02 làm trước khi có quy tắc này. Từ vựng của hai commit đó đã được bổ sung ngược vào glossary, nhưng thứ tự thì không sửa lại được — đọc `glossary.md` §8 (messaging) và §4 (truy vết) nếu cần đọc lại `Nvm.Contracts` cho thông.

> [!note] ADR viết tại chỗ quyết định, không dồn về cuối
> M0 gom cả 4 ADR vào C14. Lần này khác: `ADR-010` viết trong **C04**, `ADR-004`/`ADR-021` trong **C09**, `ADR-008` trong **C12**, `ADR-022` trong **C13**.
>
> Lý do: ADR là ảnh chụp thời điểm (`docs/adr/README.md`), viết sau vài ngày thì phần *Context* đã bị trí nhớ làm gọn lại, và những phương án bị loại — thứ đáng giá nhất — là phần rơi rụng đầu tiên. Còn phần *bằng chứng* thì rơi rụng sớm hơn nữa: output của lệnh đã chạy chỉ còn nằm trong terminal, và terminal thì đóng mất.

---

## 5. Chi tiết từng commit

### C01 — `feat(contracts): add domain event marker and event version attribute`

**Mục tiêu**: có nơi định nghĩa *"thế nào là một domain event"*, trước khi có event nào.

**Việc làm**
- Tạo `src/Platform/Nvm.Contracts/Nvm.Contracts.csproj` — **không** reference gì ngoài BCL. Đây là project đáy của cây phụ thuộc; K9 sẽ được ép bằng máy ở C17.
- `IDomainEvent`: `Guid EventId`, `DateTimeOffset OccurredAt`, `string SiteId`. Cả ba đều bắt buộc — `SiteId` vì K3, `DateTimeOffset` vì K2.
- `EventVersionAttribute(int version)` — `[AttributeUsage(AttributeTargets.Class, Inherited = false)]`, chỉ áp cho record event. K6 nói *mọi event có `[EventVersion(n)]` ngay từ v1*, nên attribute phải tồn tại **trước** event đầu tiên, không phải thêm vào sau.
- Thêm project vào `NovaVolt.Mes.slnx`.

**Kiểm chứng**
```bash
dotnet build src/Platform/Nvm.Contracts    # Build succeeded
```
Kiểm bằng mắt: `Nvm.Contracts.csproj` **không có** `<PackageReference>` nào. Nếu có, dừng lại và hỏi vì sao.

**Ghi chú thiết kế**: `IDomainEvent` cố ý **không** có `AggregateId`. Ở M1 chưa có aggregate; thêm một field mà không ai điền đúng sẽ tạo thói quen điền bừa. Nó thuộc M5.

---

### C02 — `feat(contracts): add cloudevents envelope and event type naming`

**Mục tiêu**: một chỗ duy nhất biết cách dựng chuỗi `type`, `source`, `subject` và routing key.

**Việc làm**
- `CloudEventEnvelope<TData>` — record đúng theo `scope.md` §7.4: `SpecVersion` (hằng `"1.0"`), `Id`, `Type`, `Source`, `Subject`, `Time`, `DataContentType`, `DataSchema`, `CorrelationId`, `CausationId`, `PartitionKey`, `Data`.
- `EventTypeName` — dựng và **phân tích ngược** chuỗi `com.novavolt.{context}.{event}.v{n}`. Phải parse được, không chỉ format được: `_error` queue ở C11 sẽ cần đọc `type` từ một message không deserialize nổi.
- `RoutingKey` — `nvm.{site}.{context}.{event}.v{n}` (`scope.md` §7.4). **Site giữ nguyên chữ hoa** trong routing key — xem C02.1, dòng này đã bị sửa vì bản đầu của plan ghi ngược với scope.
- `EventSource` — `urn:novavolt:{site}:{app}`.

**Kiểm chứng**
```bash
make test    # test mới của C02 xanh
```
Bộ test tối thiểu: format rồi parse ngược ra đúng giá trị ban đầu (round-trip) cho cả `EventTypeName` và `RoutingKey`; chuỗi thiếu `.vN` bị từ chối; version không phải số bị từ chối; **routing key viết `nvm.nv1.…` bị từ chối** (C02.1); `EventSource` hạ hoa site trong URN nhưng trả lại đúng `NV1` khi parse.

**Bẫy**: đừng dùng `string.ToLower()`. `CA1310` đang ở mức `warning` (= lỗi build). Dùng `ToLowerInvariant()`, và nhớ lý do ở `ADR-020`: repo này **không** bật `InvariantGlobalization`, nên culture mặc định là thật và `ToLower()` trên máy Thổ Nhĩ Kỳ cho `ı` thay vì `i`.

#### C02.1 — Site viết HOA trong routing key, viết thường trong URN

Bản đầu của plan ghi *"site và context viết thường trong routing key"*. **Sai** — ngược với `scope.md` §7.4. Ba ví dụ trong scope, đọc kỹ:

| Trường | Ví dụ trong §7.4 | Site viết thế nào |
|---|---|---|
| Routing key | `nvm.NV1.traceability.unit-serialized.v1` | **HOA** |
| `source` | `urn:novavolt:nv1:app-execution` | thường |
| `subject` | `urn:trace-unit:cell:NV1CL16238A00123` | HOA *(nằm trong serial)* |

Không phải scope tự mâu thuẫn. Quy tắc là: **từ vựng cố định viết thường** (`novavolt`, `app-execution`, `trace-unit`, `cell`, `traceability`), **định danh giữ nguyên dạng chuẩn của nó** — và dạng chuẩn của `SiteId` là chữ hoa ở khắp mọi nơi khác (`NV1CL16238A00123`, `NOVAVOLT/NV1/FORMATION/...`, claim `site_id=NV1` từ Keycloak). Hạ hoa nó trong routing key sẽ tạo ra **chỗ duy nhất** trong hệ thống mà site viết thường — đó mới là điểm không nhất quán.

`source` là ngoại lệ có lý do riêng: URN thì thành phần cố định viết thường theo thông lệ, và `nv1` ở đó đóng vai một token trong namespace chứ không phải một giá trị dữ liệu.

> [!warning] Vì sao chuyện hoa-thường này không phải chuyện vặt
> Routing key của AMQP **phân biệt hoa thường**. Publisher gửi `nvm.NV1.…` còn consumer bind `nvm.nv1.#` thì **không khớp** — và RabbitMQ không báo lỗi, không cảnh báo. Message đi vào exchange rồi biến mất. Đây là kiểu hỏng tệ nhất: im lặng hoàn toàn, và chỉ lộ ra khi có người hỏi *"sao báo cáo thiếu dữ liệu?"* ba tuần sau.
>
> Cách chặn: `RoutingKey` là **cách duy nhất** dựng chuỗi đó, và nó **từ chối** site viết thường ngay lúc parse. Không ai gõ tay routing key nữa. Ở C10, binding pattern của consumer cũng phải sinh từ `RoutingKey`, không nối chuỗi bằng tay.

---

### C03 — `test(contracts): add golden file and source-generated json context`

**Mục tiêu**: chứng minh bằng máy rằng một file JSON viết hôm nay vẫn đọc được sau này (T2).

**Việc làm**
- `NvmJsonSerializerContext : JsonSerializerContext` — `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]`. Source generator chứ không reflection: nó bắt lỗi ở **lúc build** khi một kiểu không serialize được, và AOT-safe cho sau này.
- Thư mục `tests/Contract/golden/` theo `scope.md` §10.5.
- Golden file đầu tiên: envelope của event M1 (xem C08) ở v1, **viết tay từ §7.4**, không sinh ra bằng `Serialize()` — golden file sinh bằng chính code cần kiểm là một vòng lặp tự khẳng định, không chứng minh gì.
- Project `tests/Contract/Nvm.ContractTests/` — xunit v3, cùng khuôn `tests/Unit` (xem M0/C04.1: **không** thêm `Microsoft.NET.Test.Sdk`, `<OutputType>Exe</OutputType>`).

**Kiểm chứng**
```bash
make test
```
Rồi kiểm rằng test **thật sự bắt được**: đổi một tên field trong record contract → test golden phải ĐỎ. Hoàn nguyên. Một golden test không bao giờ đỏ là một golden test không kiểm gì.

**Bẫy**: `DateTimeOffset` mặc định serialize theo ISO-8601 có offset — đúng thứ ta muốn (`2026-08-25T03:15:42.128+00:00`). Nhưng nếu ai đó lỡ để một `DateTime` lọt vào contract thì JSON sẽ **không có offset** và golden file so sánh vẫn khớp cho tới ngày đầu tiên chạy ở múi giờ khác. Đây chính là lý do `NVM002` tồn tại (C16) — golden file một mình không đủ.

---

### C04 — `feat(kernel): add icommand, icommandhandler and dispatcher`

**Mục tiêu**: khuôn command của OEF (bài 18–19 của course), viết một lần dùng cho mọi Functional Block sau này.

**Việc làm**
- Trong `src/Platform/Nvm.Kernel/` (project đã có từ M0/C04):
  - `ICommand` và `ICommand<TResult>`.
  - `IdempotencyKey` — value object bọc `Guid`, kèm `CreateVersion5(Guid namespaceId, string name)` theo đúng đoạn code `scope.md` §7.2. **UUIDv5, không phải v7** — xem C04.1. `CA5351` (SHA-1) đã được tắt có chủ đích trong `.editorconfig` đúng cho việc này.
  - `ICommandHandler<TCommand>` và `ICommandHandler<TCommand, TResult>`.
  - `ICommandDispatcher` + bản cài đặt dựng pipeline từ DI.
- `AddNvmKernel(this IServiceCollection)` — đăng ký dispatcher, quét handler trong một assembly.
- **`ADR-010` — Idempotency key = UUIDv5 từ natural key.** *Context*: bus và thiết bị đều at-least-once, nên khoá dedup phải **suy ra được từ dữ liệu** chứ không phải sinh ra. *Decision*: v5. *Alternatives*: v7 (không deterministic — vô dụng ở đây), v4 (như v7), v3 (cùng cơ chế nhưng MD5). *Consequences*: phải tự viết ~20 dòng vì BCL không có v5; phải tắt `CA5351`; và **cái bẫy SQL Server ở C04.2 mà M6 sẽ đâm vào nếu không đọc**. Kèm ba khối output ở C04.1/C04.2 làm bằng chứng tái lập được.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: cùng một natural key → `IdempotencyKey` **giống hệt nhau** qua hai lần chạy process khác nhau (deterministic); đổi một ký tự trong natural key → key khác; hai natural key khác nhau không đụng nhau; version nibble của Guid sinh ra đúng bằng `5` và variant đúng RFC 4122.

Vế cuối đáng có test riêng: nếu quên hai dòng set bit version/variant thì hàm **vẫn chạy, vẫn deterministic, vẫn dedup đúng** — nhưng sinh ra một chuỗi 128 bit không phải UUID hợp lệ. Nó chỉ lộ ra khi một hệ thống khác (Postgres `uuid`, thư viện CloudEvents, công cụ của auditor) từ chối đọc, và lúc đó dữ liệu đã nằm trong store.

Và cho dispatcher: tìm đúng handler; không có handler → ném exception có thông báo nêu tên command.

**Ghi chú**: `Nvm.Kernel` sẽ phải reference `Microsoft.Extensions.DependencyInjection.Abstractions`. Đây **không** vi phạm K9 — K9 cấm domain layer chạm EF Core / Npgsql / MassTransit, không cấm DI abstraction. Ranh giới này được ghi thành rule NetArchTest ở C17 để khỏi tranh luận lại sau.

#### C04.1 — Số version của UUID là mã thuật toán, không phải thế hệ chất lượng

Đây là chỗ trực giác đánh lừa: *"v7 mới hơn v5, chắc tốt hơn"*. Sai, và sai theo cách dẫn tới một bug im lặng.

| Version | Thuật toán | Cùng input → cùng output? | Sinh ra để giải |
|---|---|---|---|
| v3 | MD5(namespace + name) | **có** | ID suy ra được từ dữ liệu |
| v4 | ngẫu nhiên | không | ID duy nhất, không cần gì thêm |
| v5 | SHA-1(namespace + name) | **có** | như v3, hash tốt hơn |
| v7 | timestamp Unix + ngẫu nhiên | không | ID **sort được theo thời gian** |

Chỉ v3 và v5 có cột giữa là "có", và cột giữa **là toàn bộ** lý do dedup tồn tại. Đo trên máy ngày 2026-08-26:

```
== v7: goi 2 lan, cung mot thoi diem ==
  #1 01a03e70-6be0-794f-8991-c6f26cb2ec16
  #2 01a03e70-6be0-7008-9b56-ef5c173df26b
  bang nhau? False

== v5: goi 2 lan tu CUNG mot natural key ==
  #1 9df0deb6-2bef-522b-bf31-e4479213c66a
  #2 9df0deb6-2bef-522b-bf31-e4479213c66a
  bang nhau? True

== v5: doi 1 ky tu trong natural key (OCV -> ACIR) ==
  2fe37705-045f-5e6c-87fd-20bb08b15713
  bang cai tren? False
```

Đặt vào nhà máy: máy formation gửi kết quả đo OCV, không nhận được ack, gửi lại sau 800 ms. Với v7 → hai `EventId` khác nhau → **2 bản ghi cho 1 phép đo** → yield sai. Với v5 → cùng một Guid, kể cả khi lần thứ hai đi qua process khác, máy khác, hay 3 ngày sau lúc gateway flush buffer.

v7 chứa timestamp *của lúc gọi hàm* — mà lúc gọi hàm chính là thứ khác nhau giữa hai lần gửi. Nó **về bản chất** không làm được việc này.

Quy tắc rút ra, dùng cho mọi lần chọn ID về sau: **so sánh trong cùng một mục đích thì có tốt/xấu (v3 → v5, v1 → v6); khác mục đích thì không có.** Câu hỏi đúng không phải *"loại nào mới nhất"* mà *"ID này cần tính chất gì"*.

> [!note] BCL cho cái mới, không cho cái cần
> Kiểm ngày 2026-08-26 trên .NET 10: `Guid` **chỉ có** `CreateVersion7` (2 overload), **không có** `CreateVersion5`. Nên v5 phải tự viết. Đoạn code trong `scope.md` §7.2 đã chạy được, dùng thẳng.

#### C04.2 — UUIDv7 trên SQL Server KHÔNG hề sequential

Phát hiện này không dùng ở M1, nhưng **M6 sẽ đâm vào nó** nên ghi lại ngay bây giờ.

Chỗ v7 là lựa chọn đúng: khoá thay thế cho row **không có** natural key — dòng outbox (M6), bản ghi NCR mới (M9). Lý do là index locality: GUID ngẫu nhiên làm clustered key khiến mỗi insert rơi vào một chỗ ngẫu nhiên giữa bảng → page split → phân mảnh → ghi chậm dần. v7 có timestamp ở 48 bit **đầu** nên tăng dần, luôn chèn vào cuối.

Nhưng SQL Server so sánh kiểu `uniqueidentifier` **không theo thứ tự byte thông thường**. Chạy trên container `nvm-mssql` ngày 2026-08-26:

```sql
ORDER BY id  -->
  01000000-0000-0000-0000-000000000000     byte dau  = 01
  FF000000-0000-0000-0000-000000000000     byte dau  = FF
  00000000-0000-0000-0000-000000000001     byte cuoi = 01   <- dung CUOI
```

`FF00…0000` đứng **trước** `0000…0001`: SQL Server so **6 byte cuối trước**, byte đầu gần như không có tiếng nói. Mà v7 giấu timestamp ở 6 byte **đầu**.

> **Hệ quả**: v7 lưu vào cột `uniqueidentifier` vẫn chèn ngẫu nhiên y hệt v4. Bạn tưởng đã tránh được page split, thực tế thì không — và **không có gì báo cho bạn biết**.

Ba cách xử lý khi tới M6, chọn một và ghi vào ADR của outbox: lưu `binary(16)`; hoán vị byte trước khi lưu; hoặc dùng `NEWSEQUENTIALID()` cho khoá clustered và giữ v7 làm khoá nghiệp vụ.

---

### C05 — `feat(kernel): add validation, idempotency and audit behaviors`

**Mục tiêu**: pipeline behavior — chỗ mà OEF đặt các mối quan tâm cắt ngang, và là lý do `ICommandHandler` đáng tồn tại thay vì gọi thẳng method.

**Việc làm**
- **Ba** behavior, theo đúng thứ tự này:
  1. `ValidationBehavior` — command sai hình dạng thì dừng sớm, chưa đụng gì.
  2. `IdempotencyBehavior` — tra `IdempotencyKey`; đã thấy → trả kết quả cũ, **không** chạy handler. K7.
  3. `AuditBehavior` — ghi ai/lệnh gì/lúc nào, dùng `TimeProvider` (K1).
- `IIdempotencyStore` + bản `InMemoryIdempotencyStore`. Bản trên SQL Server, ghi **cùng transaction với event**, thuộc M5.

**Vì sao KHÔNG có `TransactionBehavior` ở đây** — `scope.md` §9/M1 liệt kê bốn behavior (*Validation → Idempotency → Audit → Transaction*). Behavior thứ tư cần một `IUnitOfWork` thật, mà M1 không có database nào để mở transaction lên. Viết một behavior rỗng bây giờ là để lại code chết chờ commit sau, trái [`AGENTS.md` §5.1](../../AGENTS.md). Nó vào M5 cùng event store. Thứ tự đã đặt sẵn chỗ cho nó: transaction phải là vòng **ngoài cùng** so với handler và **trong** so với idempotency.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: gửi **cùng một command hai lần** → handler chạy **đúng 1 lần**, cả hai lần gọi trả cùng kết quả (đây là bằng chứng K7); validation fail → idempotency store **không** bị ghi (thứ tự đúng); audit ghi được `OccurredAt` từ `FakeTimeProvider` chứ không phải giờ thật.

`Microsoft.Extensions.TimeProvider.Testing` đã được khai báo sẵn trong `Directory.Packages.props` từ M0/C02 cho đúng lúc này.

---

### C06 — `feat(factory-model): add isa-95 node model and invariants`

**Mục tiêu**: cây `Enterprise → Site → Area → Line → WorkCell → Equipment` với ràng buộc được ép bằng kiểu, không bằng comment.

**Việc làm**
- `src/FunctionalBlocks/FactoryModel/Nvm.FactoryModel.csproj` — Functional Block **đầu tiên**. Reference `Nvm.Contracts` và `Nvm.Kernel`, **không** reference FB nào khác (K8, ép ở C17).
- `FactoryNodeKind` enum sáu bậc. Cha hợp lệ của mỗi bậc là **cố định** — `Equipment` không thể treo thẳng vào `Site`.
- `FactoryNode` — `Code`, `Name`, `Kind`, `ParentPath`, `SiteId`, `Children`.
- `EquipmentPath` — value object cho chuỗi `NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142` (`scope.md` §2.1). Parse phải **cho phép đường dẫn ngắn hơn** (một Area cũng có path hợp lệ) và trả về `Kind` suy ra từ độ sâu.
- Ràng buộc: mọi node đều có `SiteId` (K3); node `Enterprise` là ngoại lệ duy nhất và phải được xử lý tường minh, không để `null` trôi.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: cha sai bậc bị từ chối; path 6 đoạn ra `Equipment`, 3 đoạn ra `Area`; path có đoạn rỗng (`NOVAVOLT//NV1`) bị từ chối; path phân biệt hoa thường (mã khắc và MQTT topic đều là chữ hoa — hạ hoa âm thầm ở đây sẽ đẻ ra hai node cho cùng một máy).

**Ghi chú**: `EquipmentPath` là thứ dùng lại nhiều nhất trong toàn dự án — MQTT topic, tên entity, nhãn metric, khoá phân quyền, XPath trong Mendix (`scope.md` §2.1). Sai ở đây thì sai ở sáu chỗ.

#### C06.1 — Nghiệp vụ: sáu bậc ISA-95 nghĩa là gì với người trong nhà máy

Sáu bậc không phải một cách chia thư mục cho đẹp. Mỗi bậc trả lời một câu hỏi khác nhau, và **người hỏi cũng khác nhau**:

| Bậc | Ví dụ | Ai quan tâm | Câu hỏi điển hình |
|---|---|---|---|
| Enterprise | `NOVAVOLT` | tập đoàn | so sánh yield giữa các nhà máy |
| **Site** | `NV1`, `DE1` | giám đốc nhà máy | ranh giới pháp lý, timezone, ca kíp, **và phân quyền** (K3) |
| Area | `FORMATION` | quản đốc | công đoạn này đang tắc ở đâu |
| Line | `F1`, `L1` | trưởng ca | line này ra bao nhiêu cell/giờ |
| WorkCell | `FORM-01` | kỹ sư quy trình | máy này có trôi thông số không |
| **Equipment** | `FORM-01-CH-0142` | traceability | **cell này chạy trên kênh nào** |

Hai bậc in đậm là hai bậc quan trọng nhất với dự án này, vì hai lý do khác hẳn nhau:

- **Site** là ranh giới **an ninh**. `scope.md` §5.6 nói `SiteId` là first-class ở mọi entity, event, query; rò rỉ dữ liệu DE1 sang user NV1 là lỗi bảo mật, không phải bug hiển thị.
- **Equipment** là mức mà **traceability thật sự cần**. Biết một cell "chạy ở khu FORMATION" thì vô dụng khi phải triệu hồi — cả khu có 1.000 kênh. Biết nó chạy ở kênh `CH-0142` thì khoanh vùng được đúng những cell dùng chung kênh đó.

Đây là lý do `EquipmentPath` phải parse được ở **mọi độ sâu**: dashboard của quản đốc hỏi ở mức Area, còn recall hỏi ở mức Equipment, và cả hai dùng chung một kiểu dữ liệu.

---

### C07 — `feat(factory-model): add seed for NV1 and DE1 with equipment path index`

**Mục tiêu**: có dữ liệu thật của hai site, và tra cứu được trong O(1).

**Việc làm**
- `deploy/seed/factory-model.json` — nguồn sự thật, có `revision` (số nguyên tăng dần) và `generatedAt`:
  - **NV1** (Hải Phòng, `Asia/Ho_Chi_Minh`): 7 area `ELECTRODE`, `ASSEMBLY`, `FORMATION`, `AGING`, `MODULE`, `PACK`, `WAREHOUSE`; line `L1`, `L2` (cell), `M1` (module), `P1` (pack).
  - **DE1** (Leipzig, `Europe/Berlin`) — **cố ý có DST**, xem `scope.md` §2.3. Ở M1 chỉ cần `TimeZoneId` là chuỗi trong seed; `IProductionCalendar` là M3.
  - Ít nhất một WorkCell có Equipment thật để `EquipmentPath` 6 đoạn không phải giả định: `FORM-01` với vài kênh `FORM-01-CH-0001…`.
- `FactoryModelSnapshot` — nạp seed, dựng `IReadOnlyDictionary<EquipmentPath, FactoryNode>` phẳng cho tra cứu, và giữ cây cho duyệt.
- Nạp bằng source-generated JSON của C03. Seed file được nhúng làm `EmbeddedResource` **hay** đọc từ đĩa? → đọc từ đĩa qua đường dẫn cấu hình, mặc định `deploy/seed/`. Lý do: M2 sẽ cần sửa seed mà không build lại.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: nạp seed thật (không phải fixture rút gọn) → đếm đúng số node mỗi bậc; **mọi** node không phải Enterprise đều có `SiteId` khác rỗng; không có `EquipmentPath` trùng; tra một path bất kỳ ra đúng node; tra path không tồn tại trả `null` chứ không ném.

**Bẫy**: seed là dữ liệu, nhưng test đọc nó là test **tích hợp với file**. Nếu ai đó sửa seed thì test đếm số node sẽ đỏ — đó là hành vi **đúng**, không phải phiền toái. Đừng viết test kiểu "≥ 1 node" cho dễ sống.

#### C07.1 — Nghiệp vụ: vì sao factory model phải có `revision`

Đây là chỗ dễ tưởng là chi tiết kỹ thuật thừa, nhưng nó là ràng buộc nghiệp vụ thật.

Nhà máy **thay đổi**: thêm kênh sạc vào một máy formation, tháo một work cell đi bảo trì dài hạn, đổi tên line khi chuyển đổi sản phẩm. Trong khi đó, hồ sơ traceability của một cell sản xuất **năm ngoái** đang trỏ tới `NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142` — một `equipment_path` có thể **không còn tồn tại** trong cây hôm nay.

Auditor sẽ hỏi đúng câu này (`scope.md` §6.5 đã nêu dạng của nó): *"Cell này chạy trên thiết bị nào, và lúc đó thiết bị đó thuộc line nào?"*

Nên hai điều phải đúng ngay từ M1:

- `equipment_path` trong event là **giá trị đã ghi**, không phải khoá ngoại trỏ vào cây hiện tại. Không bao giờ đi resolve nó bằng cách tra cây mới nhất rồi kết luận "không tìm thấy → dữ liệu hỏng".
- Cây có `revision`, và mỗi lần đổi là một revision mới chứ không phải sửa tại chỗ. Đây là cùng một tinh thần với K4 (append-only) áp cho master data.

Ở M1 mới chỉ cần *giữ được* `revision` và biết cái nào đang hoạt động. Truy vấn kiểu *"cây trông như thế nào vào ngày 12/3"* thuộc M10 cùng recipe effectivity — nhưng nếu M1 không đặt `revision` vào ngay bây giờ thì M10 sẽ phải chế lại lịch sử từ hư không, và không chế được.

---

### C08 — `feat(factory-model): add command to activate a model revision`

**Mục tiêu**: đi hết một vòng `ICommand → behavior → handler → domain event` **trước khi** có bus. Nếu vòng này chưa chạy thì thêm bus vào chỉ làm khó gỡ lỗi.

**Việc làm**
- `ActivateFactoryModelRevisionCommand : ICommand` — `SiteId`, `Revision`, `IdempotencyKey`.
- Handler: nạp snapshot, kiểm `revision` mới **lớn hơn** revision đang hoạt động, đổi trạng thái, trả về event.
- `[EventVersion(1)] FactoryModelRevisionActivated : IDomainEvent` — `SiteId`, `Revision`, `NodeCount`, `ActivatedAt`, `EquipmentPathsAdded`, `EquipmentPathsRemoved`.
- **Chưa publish lên bus ở commit này** — handler trả event, chưa có ai nhận. Bus vào ở C13.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: activate revision 3 khi đang ở 2 → thành công; activate lại revision 2 → bị từ chối với lý do rõ ràng; gửi **hai lần cùng một `IdempotencyKey`** → handler chạy 1 lần (K7 chạy thật trong một luồng thật, không phải trong test giả của C05); event có `[EventVersion(1)]` và mọi field thời gian là `DateTimeOffset`.

**Vì sao chọn đúng event này làm event demo của M1**: nó là event **thật**, có ích ở M2 (consumer nào cache `equipment_path` sẽ cần biết khi cây đổi), chứ không phải một `TestEvent` sinh ra để chứng minh bus chạy rồi xoá. So sánh với ba container `probe` ở M0/C05 — cũng là scaffolding, nhưng ở đó việc kiểm ranh giới mạng không có cách nào khác; ở đây thì có.

#### C08.1 — Nghiệp vụ: "kích hoạt revision" là một sự kiện, không phải một lần `UPDATE`

Câu hỏi đáng tự hỏi khi viết commit này: *vì sao phải có command và event, thay vì đọc file seed lúc khởi động là xong?*

Vì trong nhà máy, đổi cấu hình dây chuyền là một **sự kiện có hậu quả**, và nó có đủ ba thứ mà một `UPDATE` không có:

| | Nghiệp vụ hỏi gì | Ai trả lời |
|---|---|---|
| **Thời điểm** | Cây đổi lúc mấy giờ? Ca nào đang chạy lúc đó? | `ActivatedAt` trong event |
| **Nội dung đổi** | Thêm/bớt thiết bị nào? | `EquipmentPathsAdded` / `Removed` |
| **Hạ nguồn** | Ai cần biết? Service nào đang cache cây cũ? | bus (C13) |

Cột thứ ba là lý do M1 tồn tại. Ở kiến trúc không có bus, "ai cần biết" được giải quyết bằng cách mỗi service tự gọi API của FactoryModel — và khi FactoryModel chết thì cả nhà máy dừng theo. Đó chính là điều **N15** cấm, và là câu trả lời cho câu hỏi kiểm tra số 1 ở C18.

`EquipmentPathsRemoved` đáng chú ý nhất: nó là thứ khiến consumer hạ nguồn phải **quyết định** chứ không chỉ cập nhật cache. Một work cell bị gỡ khỏi cây trong khi vẫn còn WIP đứng trên đó là một tình huống thật, và nó dẫn thẳng tới bài toán hold/quarantine ở M9.

---

### C09 — `build(bus): pin masstransit 8 and add nvm.bus project`

**Mục tiêu**: chốt thư viện và ghi lại vì sao, trước khi viết dòng code bus đầu tiên.

**Việc làm**
- Thêm vào `Directory.Packages.props`, nhóm mới `Bus — M1 trở đi`:
  ```xml
  <PackageVersion Include="MassTransit" Version="8.5.10" />
  <PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.10" />
  ```
  Kèm comment nêu rõ lý do pin v8 (§2.1) — cùng khuôn với comment đã có sẵn cho Shouldly và xunit.v3.
- `src/Platform/Nvm.Bus/Nvm.Bus.csproj` — reference `Nvm.Contracts`, `MassTransit.RabbitMQ`.
- **`ADR-004` — RabbitMQ thay vì Kafka**: *Context* là ràng buộc thật của dự án (một máy 15,4 GB, quota Docker 8 GB, hai site nhỏ, cần routing theo topic chứ không cần replay log); *Alternatives* phải nêu Kafka **và** lý do loại nó không phải "nặng quá" mà là: mô hình consumer group + offset không khớp với DLQ-per-queue và retry-per-message mà MES cần, còn replay thì dự án này lấy từ event store chứ không lấy từ bus.
- **`ADR-021` — MassTransit 8 thay vì 9**: theo §2.1, kèm hai dòng nuspec làm bằng chứng và ngày kiểm.
- Cập nhật bảng index trong `docs/adr/README.md` **trong cùng commit** (quy tắc của chính file đó).

**Kiểm chứng**
```bash
dotnet restore NovaVolt.Mes.slnx      # không có NU1605/NU1608
dotnet build  NovaVolt.Mes.slnx       # Build succeeded
```
`CentralPackageTransitivePinningEnabled` đang bật từ M0/C02, nên MassTransit kéo theo `Microsoft.Extensions.* 10.0.0` sẽ bị pin. Kiểm rằng không có cảnh báo hạ cấp version — `TreatWarningsAsErrors` sẽ biến nó thành lỗi build, và đó là hành vi mong muốn.

#### C09.1 — Kiểm license trước khi pin, không chỉ kiểm nó chạy

Lệnh dùng để phát hiện §2.1, giữ lại vì sẽ dùng cho mọi package sau này:

```bash
curl -s "https://api.nuget.org/v3-flatcontainer/masstransit/9.2.0/masstransit.nuspec" | grep -i license
curl -s "https://api.nuget.org/v3-flatcontainer/masstransit/8.5.10/masstransit.nuspec" | grep -i license
```

Bản mới nhất trên NuGet **không** đồng nghĩa với bản dùng được. Đây là lần thứ hai trong dự án (lần đầu: FluentAssertions v8).

---

### C10 — `feat(bus): add exchange, routing key and queue topology`

**Mục tiêu**: topology là **quyết định kiến trúc**, không phải cấu hình. Đặt sai bây giờ thì mọi consumer sau này phải sống chung với nó.

**Việc làm**
- `NvmEndpointNameFormatter` — tên queue theo context và vai trò, không theo tên class C#. Tên class đổi thì queue không được đổi theo: đổi tên queue trên hệ thống đang chạy nghĩa là bỏ lại một queue cũ còn message trong đó.
- Exchange theo **context** (`traceability`, `production-execution`, `quality`, `factory-model`), routing key `nvm.{site}.{context}.{event}.v{n}` từ `RoutingKey` của C02.
- `AddNvmBus(this IServiceCollection, Action<NvmBusOptions>)` — cấu hình host RabbitMQ đọc từ `.env` qua `DotEnvLoader` đã có ở M0/C10.2. **Không** thêm file cấu hình thứ hai chứa mật khẩu.
- `e.SetQuorumQueue()` cho mọi receive endpoint.

**Kiểm chứng**
```bash
make up
dotnet run --project src/Apps/Nvm.Host.All
docker exec nvm-rabbitmq rabbitmqctl -q list_connections user peer_host state
```
Kỳ vọng: một connection của user `nvm` ở trạng thái `running`, và log host in `Bus started: rabbitmq://localhost/`.

#### C10.3 — Không kiểm được exchange/queue ở C10, và lý do đáng biết

Bản đầu của plan ghi kiểm chứng C10 là `list_queues` + `list_exchanges`. **Chạy thử: cả hai đều rỗng.**

RabbitMQ khai báo topology **lười**: exchange chỉ ra đời khi có publisher gửi lần đầu, queue chỉ ra đời khi có consumer đăng ký. C10 chưa có cái nào — publisher là C13, consumer cũng C13. Cấu hình topology đúng đến mấy cũng chưa để lại dấu vết nào trên broker.

Nên C10 chia làm ba loại bằng chứng, và đó là cách chia đúng cho mọi commit "quy ước":

| Kiểm cái gì | Bằng cách nào | Ở đâu |
|---|---|---|
| **Quy ước** — tên exchange, binding pattern, tên queue, từ chối site chữ thường | unit test, không cần hạ tầng | C10 |
| **Kết nối** — đọc `.env` đúng, credential đúng, MassTransit 8 nói chuyện được với RabbitMQ 4.3.5 | `list_connections` | C10 |
| **Topology thật trên broker** — exchange `nvm.factory-model` kiểu `topic`, queue `quorum` | `list_exchanges` / `list_queues` | **C13** |

Dời hàng thứ ba xuống C13, nơi nó **thật sự chạy được**. Giữ ở C10 chỉ tạo ra một bước kiểm luôn rỗng — và một bước kiểm không bao giờ đỏ được thì không kiểm gì (cùng bài học với golden file ở C03).

#### C10.1 — RabbitMQ 4.x đã bỏ classic mirrored queue

Image đang chạy là `rabbitmq:4.3.5-management` (M0/C08). Từ RabbitMQ 4.0, **classic queue mirroring bị gỡ bỏ**; thứ thay thế cho queue cần bền là **quorum queue**. Trên một node dev thì cả hai đều "chạy", nên sai lầm này không lộ ra ở M1 — nó lộ ra khi có node thứ hai.

Đặt `SetQuorumQueue()` ngay bây giờ vì đổi loại queue sau này **không** làm được tại chỗ: phải xoá queue và tạo lại, tức là phải xử lý message đang nằm trong đó.

#### C10.2 — Đừng để MassTransit tự đặt tên queue theo tên class

Mặc định, MassTransit sinh tên endpoint từ tên consumer class. Tiện lúc bắt đầu, nhưng nó buộc **tên C# thành contract vận hành**: đổi `FactoryModelCacheConsumer` thành `EquipmentPathCacheConsumer` là đổi tên queue, và queue cũ ở lại broker với message chưa xử lý mà không ai để ý.

`SetKebabCaseEndpointNameFormatter()` chỉ đổi cách viết, không giải quyết vấn đề gốc. Dùng formatter riêng đặt tên tường minh theo context + vai trò.

---

### C11 — `feat(bus): add retry, redelivery and dead letter policy`

**Mục tiêu**: message hỏng phải dừng ở một chỗ đọc được, không biến mất và cũng không quay vòng vô hạn.

**Việc làm**
- `UseMessageRetry` với `Exponential` — 5 lần thử (D2 nói *"ném exception 5 lần"*).
- `_error` và `_skipped` queue: MassTransit tạo tự động theo `<queue>_error` / `<queue>_skipped`. Việc của commit này là **kiểm chứng** chúng tồn tại và đọc được, không phải tạo chúng.
- `UseKillSwitch` — cân nhắc: nếu tỉ lệ lỗi vượt ngưỡng thì tạm dừng endpoint thay vì quay vòng đốt CPU. Nếu thêm thì phải có test; nếu không thêm thì ghi một dòng lý do trong `Nvm.Bus/README.md`.

#### C11.1 — MassTransit v8 KHÔNG có tuỳ chọn jitter

`scope.md` §9/M1 ghi *"retry policy (exponential + jitter)"*. Nhưng danh sách policy của MassTransit v8 là: `Immediate`, `Interval`, `Intervals`, `Exponential`, `Incremental` — **không có tham số jitter**.

Hai cách, chọn cách thứ hai:
- Bỏ jitter, dùng `Exponential` thuần. Đơn giản, nhưng mất đúng thứ jitter sinh ra để chống: nhiều consumer cùng fail vì một nguyên nhân chung (broker nghẽn, DB chậm) sẽ retry **đồng pha**, tạo sóng tải lặp lại đúng lúc hệ thống đang yếu nhất.
- `r.Intervals(...)` với dãy khoảng đã cộng nhiễu tính sẵn từ `Random.Shared`, **cộng một dòng comment nói rõ vì sao không dùng `Exponential`**.

`scope.md` §9/M1 **đã được sửa** ngày 2026-08-26 cho khỏi trôi (`AGENTS.md` §2.4) — nó nêu đúng năm policy có thật và ghi rõ jitter phải tự cộng.

#### C11.2 — Delayed message exchange plugin KHÔNG có trong image

`UseDelayedRedelivery` của MassTransit cần plugin `rabbitmq_delayed_message_exchange`. Plugin đó là **community plugin, không đi kèm image chính thức** `rabbitmq:*-management` — phải tải `.ez` và `rabbitmq-plugins enable` thủ công.

Quyết định: **không cài**. M1 chỉ dùng retry in-memory. Redelivery theo lịch (phút, giờ, ngày) là nhu cầu của saga, và `ADR-015` trong `scope.md` §5.7 đã chốt sẵn hướng đi khác — **Quartz store** — cho M7. Cài plugin bây giờ là dựng một cơ chế mà ADR đã quyết định không dùng.

Hệ quả cần biết: retry in-memory **giữ message trong bộ nhớ consumer trong lúc chờ**. Với 5 lần thử và khoảng cách tính bằng giây thì không sao. Đừng nâng khoảng cách lên phút — masstransit.io cảnh báo đúng chỗ này: consumer có concurrency 5 mà retry interval 1 giờ thì 5 message hỏng làm nghẽn endpoint suốt 1 giờ.

**Kiểm chứng**: gộp vào C13, vì cần consumer thật mới đo được.

---

### C12 — `feat(bus): carry cloudevents attributes as transport headers`

**Mục tiêu**: envelope §7.4 không chỉ nằm trong tài liệu — nó phải nhìn thấy được trên message thật.

> Nền của commit này là §3.2 — đọc mục đó trước, đặc biệt bảng phân biệt *envelope của framework* với *envelope nghiệp vụ*.

**Việc làm**
- `CloudEventsPublishFilter` — filter phía gửi, ghi các thuộc tính CloudEvents thành header AMQP: `ce_specversion`, `ce_id`, `ce_type`, `ce_source`, `ce_subject`, `ce_time`, `ce_dataschema`, cộng `correlationid` / `causationid` / `partitionkey`.
- `CloudEventsConsumeFilter` — đọc ngược, đưa vào một `CloudEventContext` mà consumer lấy được qua DI scope.
- `ce_id` **phải bằng** `IdempotencyKey` khi event sinh ra từ một command — đây là mối nối giữa §7.2 và §7.4, và là thứ làm dedup ở M2 khả thi. Nếu hai giá trị này lệch nhau thì mỗi tầng dedup theo một khoá khác nhau và cả hai đều vô dụng.
- **`ADR-008` — CloudEvents envelope**: ghi cả quyết định lẫn phần **không** làm. *Consequences* phải nêu ba điều: (1) message trên dây không phải CloudEvents thuần; (2) tiền tố `ce_` là **quy ước nội bộ**, vì AMQP 0-9-1 không có binding chính thức (§3.2); (3) envelope đầy đủ §7.4 vẫn là contract của event store ở M6. *Alternatives* nêu phương án raw JSON và lý do loại (K10 — Mendix không đọc bus).

**Kiểm chứng**
```bash
# Publish 1 message với consumer đang TẮT, rồi đọc thẳng từ broker, không qua MassTransit.
# reject_requeue_true: xem xong trả message lại queue, không tiêu thụ mất.
docker exec nvm-rabbitmq rabbitmqadmin -u nvm -p nvm_dev_only \
  get messages --queue <queue> --ack-mode reject_requeue_true
```
Kỳ vọng: thấy đủ 7 header `ce_*` với giá trị đúng, và `ce_id` khớp `IdempotencyKey` của command đã gửi.

Đọc bằng công cụ **ngoài** MassTransit là phần quan trọng của phép kiểm này: nếu chỉ kiểm bằng chính consumer MassTransit thì không phân biệt được "header có thật trên dây" với "MassTransit tự nhớ trong process".

#### C12.1 — Image ship `rabbitmqadmin` v2, cú pháp khác mọi ví dụ trên mạng

Đã kiểm trong container đang chạy: `rabbitmqadmin --version` → **2.34.0**, nằm ở `/usr/local/bin/rabbitmqadmin`.

Bản v1 (script Python đi kèm management plugin của RabbitMQ 3.x) dùng cú pháp `key=value`:
`rabbitmqadmin get queue=my-queue ackmode=reject_requeue_true`. Gần như **toàn bộ** ví dụ tìm được trên blog và Stack Overflow là cú pháp đó, và nó **không chạy** ở đây.

v2 là một CLI khác hẳn, dùng subcommand + cờ dài: `get messages --queue … --ack-mode …`, `list queues`, `list exchanges`. Nó cũng cần credential tường minh (`-u` / `-p`) vì M0/C08 đã thay `guest` bằng user riêng.

Hai lệnh dưới đây đã chạy được trong container ngày 2026-08-26 và dùng làm chuẩn cho mọi bước kiểm chứng của M1:

```bash
docker exec nvm-rabbitmq rabbitmq-diagnostics -q list_queues name type messages
docker exec nvm-rabbitmq rabbitmqctl -q list_exchanges name type
```

---

### C13 — `feat(bus): add bus probe worker with two independent consumers`

**Mục tiêu**: đây là commit sinh ra bằng chứng cho **D1**, **D2** và **D4**.

**Việc làm**
- `src/Workers/Nvm.BusProbe/` — worker riêng, **process riêng** với `Nvm.Host.All`. Hai consumer:
  - `FactoryModelCacheProbe` — nhận `FactoryModelRevisionActivated`, log `revision` + `nodeCount`.
  - `FactoryModelAuditProbe` — nhận **cùng** event, log sang dòng khác.

  Hai consumer, **hai receive endpoint riêng** → hai queue riêng cùng bind vào một exchange. Đây là điểm mấu chốt của D1: nếu hai consumer dùng chung một queue thì mỗi message chỉ **một** trong hai nhận được — đó là competing consumer, không phải fan-out.
- Consumer thứ ba `FailingProbe` — ném exception có chủ đích, bật bằng biến môi trường. Phục vụ D2.
- Phía `Nvm.Host.All`: endpoint dev-only `POST /dev/bus/activate-revision` gửi `ActivateFactoryModelRevisionCommand` của C08, và handler publish event ra bus. **Chỉ đăng ký khi `IsDevelopment()`** — cùng khuôn với `DotEnvLoader` ở M0/C10.2.
- `Makefile`: `make bus-fanout`, `make bus-dlq`, `make bus-chaos`.

**Kiểm chứng — D1**

| Kiểm | Kỳ vọng | Kết quả |
|---|---|---|
| `make bus-fanout` | log của `Nvm.BusProbe` có **2 dòng** cho **1** lần publish | ✅ **2 dòng** — `cache-updater received revision 1` và `audit-trail recorded revision 1`, cùng `ce_id` |
| `list_queues name type messages` | **2** queue riêng, tên theo quy ước C10 | ✅ `nvm.factory-model.cache-updater` và `nvm.factory-model.audit-trail`, cả hai **quorum** |
| `list_exchanges name type` | exchange `nvm.factory-model` kiểu `topic` (hàng thứ ba của bảng C10.3) | ✅ `nvm.factory-model  topic` |
| Tắt 1 consumer rồi publish | queue của consumer đang tắt **tăng** `messages`, consumer còn lại vẫn nhận | ✅ `audit-trail` = **1**, `cache-updater` = **0** và vẫn in dòng nhận |

Dòng thứ ba là phép kiểm quan trọng nhất: nó chứng minh hai queue **độc lập thật**, chứ không phải hai consumer tình cờ cùng chạy.

**Kiểm chứng — D2**

| Kiểm | Kỳ vọng | Kết quả |
|---|---|---|
| `make bus-dlq` | log có **đúng 5** lần thử | ✅ **5**, đánh số 1→5. Khoảng cách 245 / 480 / 920 / 1933 ms — exponential có jitter |
| `<queue>_error` | có **1** message | ✅ `nvm.factory-model.failing-probe_error  quorum  1` |
| queue chính | **0** message | ✅ `nvm.factory-model.failing-probe  quorum  0` |
| Nội dung message trong `_error` | payload nguyên vẹn + header `MT-Fault-*` nêu exception | ✅ `MT-Fault-ExceptionType`, `MT-Fault-Message` (*"attempt 5 of 5"*), `MT-Fault-RetryCount: 4`, và **cả 6 header `ce_*` còn nguyên** |

> `MT-Fault-RetryCount` = **4**, không phải 5: nó đếm lần thử **lại**. Đúng cái off-by-one mà
> `NvmRetryPolicy.MaxAttempts` đã đặt tên để tránh — và là lý do log của consumer, không phải header
> của broker, mới là bằng chứng cho "5 lần".

#### C13.1 — Đếm số lần thử, đừng đếm số dòng log

Retry của MassTransit chạy **trong cùng một lần consume**; nó không đẩy message qua lại broker. Nên `list_queues` **không** cho thấy 5 lần thử — nó chỉ cho thấy trạng thái cuối. Bằng chứng cho "5 lần" phải là log của consumer, và log phải in **số thứ tự lần thử**, không phải in cùng một dòng năm lượt.

Cùng loại bẫy với M0/C05: xác nhận nó xảy ra **đúng lý do**, không chỉ xác nhận nó xảy ra.

#### C13.2 — `_error` queue chỉ tồn tại sau lần lỗi đầu tiên

Đừng kiểm sự tồn tại của `<queue>_error` **trước** khi có message lỗi rồi kết luận cấu hình sai. MassTransit khai báo queue này lúc cần. Thứ tự đúng: chạy `make bus-dlq` → rồi mới `list_queues`.

#### C13.3 — Lab phá hoại: tắt RabbitMQ giữa lúc publish

> Đây là commit sinh ra **con số quan trọng nhất của M1**. Lý do đổi DoD ở §3.3 — đọc trước khi chạy, để biết mình đang đo cái gì.

Kịch bản, chạy bằng `make bus-chaos`:

1. Publish liên tục N event (N ghi rõ, ví dụ 200), mỗi event có số thứ tự trong `data`.
2. Giữa chừng: `docker compose stop rabbitmq`.
3. Chờ 30 s, `docker compose start rabbitmq`.
4. Đếm số event mà consumer **thực sự nhận được**, đối chiếu với N.

Đã chạy **2026-08-27**, `CHAOS_COUNT=200`, `CHAOS_DELAY_MS=100`, timeout publish 2 s,
`CHAOS_STOP_AFTER=5`, `CHAOS_DOWNTIME=30`.

| Kiểm | Kỳ vọng | Kết quả |
|---|---|---|
| App có crash không | **không**, `/health/live` = `Healthy` suốt | ✅ `Healthy` suốt; `Application started` xuất hiện **đúng 1 lần** |
| `/health/ready` trong lúc broker tắt | `Unhealthy` ở đúng check của bus, các check khác không lan | ✅ chỉ `rabbitmq` đỏ; 5 check còn lại xanh |
| Publish thất bại | bị **bắt và đếm**, có log nêu rõ số | ✅ `failed: 18`, `firstFailure: 48`, `lastFailure: 65`, mỗi lần một dòng `LogWarning` |
| Số event mất | **> 0** — và con số này là kết quả chính của lab | ✅ **18 / 200**. `published` = `nhận được` = **182** |
| Sau khi broker lên lại | publish tiếp tục thành công **không cần restart app** | ✅ 135 event sau `lastFailure` đều thành công, không restart |

Đoán trước khi chạy (`AGENTS.md` §5.8.4): chủ repo đoán **11–25**. Đo được **18** — đoán đúng khoảng,
và lý do đoán đúng cũng đúng: cửa sổ mất bị chặn bởi **timeout 2 s mỗi lần publish**, không bởi nhịp
100 ms. 30 giây broker chết chỉ đủ cho 18 lần thử, chứ không phải 300.

Con số ở dòng 4 vào `benchmarks.md` và `ADR-022`. **Không được làm tròn về 0, không được bỏ qua vì "chưa có outbox".** Đó chính là điều lab muốn cho thấy: bus một mình không đủ để đạt N3, và outbox ở M6 tồn tại vì con số này chứ không vì nó là một pattern nổi tiếng.

Dòng cuối cũng quan trọng không kém: MassTransit tự nối lại. Nếu phải restart app thì có gì đó sai trong cấu hình host, và nó sẽ thành sự cố vận hành thật ở M13.

---

### C14 — `feat(host): add bus health check without breaking startup`

**Mục tiêu**: `/health/ready` biết thêm về bus, mà **không** đánh mất tính chất đã chứng minh ở M0/C10 — app sống khi dependency chết.

**Việc làm**
- `AddMassTransit` **tự đăng ký** một health check cho bus (gói kéo theo `Microsoft.Extensions.Diagnostics.HealthChecks`). Việc của commit này là kiểm nó được gắn tag `ready` **và không** gắn vào `live`.
- Cập nhật `HealthCheckRegistration.cs` (M0/C10) — số check đi từ 5 lên 6.
- Cập nhật `HealthReportWriter` nếu cần để tên check của MassTransit hiển thị dễ đọc.

**Kiểm chứng — đây là regression của D5 ở M0**

| Kiểm | Kỳ vọng | Kết quả |
|---|---|---|
| 6 probe lúc bình thường | tất cả `Healthy` | ✅ `bus`, `keycloak`, `minio`, `postgres`, `rabbitmq`, `sqlserver`. `live` vẫn chỉ có `self` |
| **Khởi động app khi RabbitMQ đang tắt** | app lên được, `live=Healthy`, `ready=Unhealthy[bus]` | ✅ lên sau **249 ms**; `live=Healthy`; `ready=Unhealthy` ở `bus` (*"Not ready: not started"* / *"Broker unreachable"*) **và** `rabbitmq`; 4 probe khác xanh |
| Tắt SQL Server (D5 của M0) | vẫn phát hiện < 10 s, các check khác không lan | ✅ **3157 ms** (M0: 3,2 s). Chỉ `sqlserver` đỏ |
| Tổng thời gian `/health/ready` | ghi vào `benchmarks.md`, so với 451 ms / 10,4 ms của M0 | ✅ **272,3 ms** lần đầu, **7,9 / 7,0 ms** lần sau — nhanh hơn M0 dù nhiều hơn một probe |
| Bật lại broker | `ready` xanh lại, **không** restart app | ✅ **19,8 s** và **4,7 s** ở hai lần đo; `Application started` đúng 1 lần |

Dòng thứ hai là dòng phải chạy thật, không suy luận. Đây đúng loại bẫy đã gặp ở M0/C10.3 với RabbitMQ: thư viện health check muốn có sẵn một `IConnection` trong DI, và tạo connection **lúc đăng ký service** làm app không khởi động nổi khi broker tắt. MassTransit khởi động bus bằng `IHostedService` nên về lý thuyết không vướng, nhưng "về lý thuyết" không phải bằng chứng. **Đã chạy: không vướng.**

#### C14.1 — `bus` mù với đúng kịch bản nguy hiểm nhất

Health check của MassTransit nói về **bus trong process này**: đã khởi động chưa, receive endpoint sẵn sàng chưa. Nó **không** phải probe của broker.

Hệ quả đo được: `Nvm.Host.All` chỉ publish nên không có receive endpoint nào, và khi bus đã khởi động xong thì không còn gì để báo hỏng. Tắt broker giữa chừng → `bus` báo **`Healthy` liên tục 152 giây**.

| | broker chết **trước** khi app khởi động | broker chết **sau** khi bus đã chạy |
|---|---|---|
| `bus` | ✅ bắt được | ❌ Healthy suốt |
| `rabbitmq` (HTTP tới management API) | ✅ bắt được | ✅ bắt được |

Nên **giữ cả hai**, và lý do phải được ghi lại chứ không để ai đó sau này xoá `rabbitmq` vì "MassTransit đã có health check rồi". Ngược lại `bus` cũng không xoá được: nó là thứ giữ `ready` đỏ trong lúc bus đang khởi động, trước khi có connection nào để probe kia kiểm.

#### C14.2 — Đặt tên và gắn tag cho health check, đừng để thư viện tự chọn

`AddMassTransit` tự đăng ký health check tên `masstransit-bus` với tag do chính nó chọn. Cả hai đều là **sự kiện vận hành**: tên xuất hiện trong dashboard và runbook, còn tag quyết định probe trả lời ở endpoint nào — cùng loại lý do đã đặt cho tên queue ở C10.2.

Nên C14 khai tường minh qua `ConfigureHealthCheckOptions`: tên `bus` (đặt theo **thứ nó kiểm**, giống `sqlserver`/`postgres`, không theo tên thư viện), tag đúng `ready` và **không** `live`, `MinimalFailureStatus = Unhealthy`.

Dòng cuối không phải chi tiết vụn: `Degraded` trả HTTP **200**, nên một bus báo `Degraded` sẽ để instance ở lại trong rotation trong khi nó không chuyển nổi message. Ba điều này được ép bằng test (`BusHealthCheckTests`) chứ không bằng comment — nâng version MassTransit mà mặc định đổi thì build đỏ.

---

### C15 — `feat(analyzers): add NVM001 forbidding DateTime.UtcNow`

**Mục tiêu**: K1 được ép bởi compiler, không bởi trí nhớ. Đây là bằng chứng cho **D3**.

**Việc làm**
- `tools/analyzers/Nvm.Analyzers/Nvm.Analyzers.csproj`:
  - `<TargetFramework>netstandard2.0</TargetFramework>` — **ghi đè** `net10.0` của `Directory.Build.props`. File đó đã ghi sẵn trường hợp ngoại lệ này trong comment đầu file.
  - `Microsoft.CodeAnalysis.CSharp` **5.6.0** (§2.2), `PrivateAssets="all"`.
  - `Microsoft.CodeAnalysis.Analyzers` — bật bộ rule `RS*` cho chính analyzer.
  - `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`.
- `Nvm001DateTimeNowAnalyzer` — bắt `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow`, `DateTimeOffset.Now`. Thông báo phải **nêu cách sửa**: *"dùng `TimeProvider.GetUtcNow()`; xem AGENTS.md K1"*.
- Ngoại lệ: chính chỗ đăng ký `TimeProvider.System` phải được phép. Cách sạch nhất là dùng `TimeProvider.System` (không đụng `DateTime.UtcNow`), nên có thể **không cần** ngoại lệ nào — kiểm lại `Program.cs` trước khi viết cơ chế suppress.
- Nối analyzer vào mọi project bằng **`Directory.Build.targets`** (file mới, không phải `.props` — xem C15.2):
  ```xml
  <ItemGroup Condition="'$(IsAnalyzerProject)' != 'true'">
    <ProjectReference Include="$(MSBuildThisFileDirectory)tools/analyzers/Nvm.Analyzers/Nvm.Analyzers.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>
  ```
  và `<IsAnalyzerProject>true</IsAnalyzerProject>` trong `Nvm.Analyzers.csproj`.
- `.editorconfig`: `dotnet_diagnostic.NVM001.severity = error`.

**Kiểm chứng — đây là D3**

| # | Bước | Kỳ vọng | Kết quả |
|---|---|---|---|
| 1 | `make build` trên code hiện tại | xanh — repo đã dùng `TimeProvider` từ M0/C03 | ✅ xanh, **0** vi phạm sẵn có |
| 2 | Thêm `var x = DateTime.UtcNow;` vào một file bất kỳ | `dotnet build` **FAILED**, `error NVM001` | ✅ `error NVM001` tại đúng dòng, build FAILED |
| 3 | Đổi thành `DateTimeOffset.Now` | vẫn FAILED | ✅ FAILED |
| 3b | Đổi thành `using Clock = System.DateTime; Clock.UtcNow` | vẫn FAILED, và thông báo nêu `DateTime` chứ không nêu `Clock` | ✅ FAILED, thông báo đúng |
| 3c | Đổi thành `DateTime.Today` | vẫn FAILED | ✅ FAILED |
| — | **Đối chứng**: `clock.GetUtcNow()` với `TimeProvider` được inject | **xanh** | ✅ xanh |
| 4 | Hoàn nguyên | xanh trở lại | ✅ xanh |
| 5 | `make ci` | xanh | ✅ xanh, 247 test |

Bước 3 tồn tại vì một analyzer chỉ bắt đúng một chuỗi ký tự là analyzer dễ lách nhất.

Ba bước không có trong plan gốc, thêm vì cùng lý do đó:

- **3b** kiểm chính lập luận "khớp theo symbol chứ không theo chữ". Không có nó thì câu đó chỉ là một comment.
- **3c** vì `DateTime.Today` là cùng một lỗi (đọc đồng hồ máy) và plan không liệt kê. Đã thêm vào danh sách cấm.
- **Đối chứng** vì một analyzer báo lỗi *mọi* property reference cũng qua được cả bốn bước trên.

#### C15.4 — `dotnet --version` trong comment XML làm hỏng CPM, im lặng

Khi ghi lệnh đo lại Roslyn version vào comment của `Directory.Packages.props`, chuỗi `--` trong
`dotnet --version` làm file **không well-formed** (XML cấm `--` bên trong comment).

Triệu chứng **không** phải "XML sai": MSBuild bỏ qua file, Central Package Management tắt theo, và
mọi project báo `NU1015: PackageReference không có version` — tám project cùng lúc, không project nào
nhắc tới `Directory.Packages.props`. Mất một lượt build mới lần ra.

Ghi lại vì loại comment "lệnh để đo lại" đang được khuyến khích khắp repo này, và bất kỳ lệnh nào có
cờ dài (`--verify-no-changes`, `--no-restore`, `--nologo`) đều dính.

#### C15.1 — Analyzer im lặng là chế độ hỏng mặc định

Ba cách một analyzer không chạy mà **không báo lỗi gì**:

1. **Roslyn không khớp** — build với `Microsoft.CodeAnalysis.CSharp` mới hơn compiler thì ra `AD0001`, và `AD0001` mặc định là warning nên dễ trôi qua trong lượng log build. §2.2.
2. **Thiếu `OutputItemType="Analyzer"`** — nó thành một reference thư viện bình thường, biên dịch được, không phân tích gì.
3. **Không có `.editorconfig` severity** — diagnostic mặc định `Info` thì không chặn build kể cả khi `TreatWarningsAsErrors` đang bật.

Cả ba đều cho ra cùng triệu chứng: build xanh. Nên **bước 2 của bảng kiểm chứng là bắt buộc** — một analyzer chưa từng làm đỏ build là một analyzer chưa được chứng minh là đang chạy. Cùng bài học với M0/C10.4: build xanh không đồng nghĩa quy ước đang được ép.

#### C15.2 — Nối analyzer trong `.props` là sai chỗ, phải là `.targets`

`Directory.Build.props` áp cho **mọi** project, kể cả chính `Nvm.Analyzers`. Không có `Condition` thì analyzer tham chiếu chính nó → MSBuild báo vòng phụ thuộc.

Phản xạ đầu tiên — đặt `<IsAnalyzerProject>true</IsAnalyzerProject>` trong `Nvm.Analyzers.csproj` rồi điều kiện hoá `ItemGroup` trong `Directory.Build.props` — **không chạy**. `Directory.Build.props` được nạp **trước** nội dung `.csproj`, nên lúc điều kiện được đánh giá thì property đó chưa tồn tại và điều kiện luôn đúng.

Đây là lý do `ItemGroup` nối analyzer nằm ở **`Directory.Build.targets`** (nạp **sau** `.csproj`), không nằm ở `.props`. Cùng họ với bài học M0/C04.3: `Directory.Build.props` có thứ tự nạp riêng, và giả định sai về thứ tự đó cho ra hành vi im lặng chứ không cho ra lỗi.

Cách thay thế nếu vì lý do nào đó phải giữ ở `.props`: điều kiện theo đường dẫn, `Condition="!$(MSBuildProjectDirectory.Contains('analyzers'))"`. Thô hơn và gãy khi đổi tên thư mục — chỉ dùng khi có lý do ghi ra được.

#### C15.3 — Test analyzer: gói `.XUnit` kẹt ở xunit v2

`Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit` bản mới nhất là `1.1.2` và kéo `Microsoft.CodeAnalysis.Testing.Verifiers.XUnit` — hạ tầng **xunit v2**. Repo này chỉ có xunit v3 (M0/C04.1), nên gói đó sẽ kéo ngược v2 vào và tạo ra đúng loại xung đột mà M0 đã tốn thời gian gỡ.

Dùng gói **không** ràng buộc framework test: `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` **1.1.4** với `DefaultVerifier`. Nó không phụ thuộc xunit ở bất kỳ version nào (đã kiểm nuspec: chỉ `Microsoft.CodeAnalysis.Analyzer.Testing` + `Microsoft.CodeAnalysis.CSharp.Workspaces`).

Nếu cách đó vẫn vướng, phương án dự phòng là bảng 5 bước ở trên — vi phạm cố ý rồi kiểm mã thoát của `dotnet build`. Thô hơn nhưng kiểm đúng thứ cần kiểm, và không kéo thêm phụ thuộc nào.

---

### C16 — `feat(analyzers): add NVM002 and NVM003 for event contracts`

**Mục tiêu**: K2 và K6 cũng được ép bằng máy.

**Việc làm**
- **`NVM002`** — cấm `DateTime` (kiểu, không phải property tĩnh) trong bất kỳ type nào cài `IDomainEvent`, và trong mọi type nằm trong `Nvm.Contracts`. Phải bắt cả: property, field, tham số constructor của record, và `DateTime` bên trong generic (`List<DateTime>`, `Dictionary<string, DateTime>`).
- **`NVM003`** — mọi type cài `IDomainEvent` phải có `[EventVersion(n)]` với `n >= 1`. Đây là K6, và nó phải chạy **từ v1** — thêm attribute sau khi đã có event ngoài production thì đã muộn.
- `.editorconfig`: cả hai ở mức `error`.

**Kiểm chứng**

| # | Vi phạm cố ý | Kỳ vọng | Kết quả |
|---|---|---|---|
| 0 | Build repo hiện tại | xanh — contract đang dùng `DateTimeOffset` | ✅ xanh, **0** vi phạm sẵn có |
| 1 | Thêm `DateTime Foo` vào một event | `error NVM002` | ✅ FAILED, trỏ đúng tham số |
| 2 | Thêm `IReadOnlyList<DateTime> Bar` | `error NVM002` | ✅ FAILED |
| 2b | `DateTime? Maybe` và `Dictionary<string, DateTime[]> Deep` | `error NVM002` cho **cả hai** | ✅ FAILED, 2 lỗi riêng biệt |
| 3 | Bỏ `[EventVersion(1)]` khỏi `FactoryModelRevisionActivated` | `error NVM003` | ✅ FAILED, trỏ vào tên type |
| 4 | `[EventVersion(0)]` | `error NVM003` | ✅ FAILED, trỏ vào attribute |
| 4b | `[EventVersion(-1)]` | `error NVM003` | ✅ FAILED |
| — | **Đối chứng**: `DateTimeOffset` + `[EventVersion(1)]` | xanh | ✅ xanh |
| 5 | Hoàn nguyên cả 4 | `make ci` xanh | ✅ xanh, **262** test |

Bước 2 tách riêng vì phân tích kiểu lồng nhau là chỗ analyzer hay bỏ sót nhất, và một `DateTime` giấu trong `List<>` cũng phá K2 y hệt. Bước 2b thêm vào vì `DateTime?` thực chất là `Nullable<DateTime>` — cùng một phép đệ quy, nhưng nếu chỉ so kiểu ngoài cùng thì nó lọt.

#### C16.1 — Phạm vi của NVM002: ba điều kiện HOẶC, không phải một

Plan viết *"trong bất kỳ type nào cài `IDomainEvent`, và trong mọi type nằm trong `Nvm.Contracts`"*. Khi viết ra thì hoá ra là **ba** điều kiện, và mỗi cái bắt được thứ hai cái kia bỏ sót:

| Điều kiện | Bắt được cái gì |
|---|---|
| cài `IDomainEvent` | event định nghĩa ở assembly khác — M2 trở đi mỗi FB sẽ có event riêng |
| assembly tên `Nvm.Contracts` | type trong project contract nhưng bị đặt sai namespace |
| namespace bắt đầu bằng `Nvm.Contracts.` | type **chưa** là event. Đây là trường hợp hay gặp nhất: một record hôm nay chưa phải event, milestone sau thành payload của một event |

Điều kiện thứ ba cũng là cái duy nhất test được bằng snippet — hai cái kia phụ thuộc tên assembly, mà `CSharpAnalyzerTest` luôn biên dịch dưới tên `TestProject`. Chúng được kiểm bằng bảng 5 bước ở trên, chạy trong assembly thật.

#### C16.2 — Property và field cùng lúc: cẩn thận backing field

Đọc cả `IPropertySymbol` lẫn `IFieldSymbol` là đúng — K2 không quan tâm bạn khai kiểu gì. Nhưng một auto-property sinh ra một **backing field cùng kiểu**, nên bản viết thẳng tay báo **hai lỗi trên một dòng**. Hai lỗi cho một sai là cách nhanh nhất để người đọc kết luận analyzer hỏng.

Lọc bằng `IsImplicitlyDeclared` trên field (backing field luôn implicit), **không** lọc trên property (tham số positional record đến đây dưới dạng property, và lọc nhầm là tắt mất trường hợp chính). Có một test riêng đếm đúng **hai** diagnostic cho một class có một field và một property — framework test sẽ đỏ nếu xuất hiện cái thứ ba.

**Nếu quá giờ**: `NVM003` là ứng viên cắt đầu tiên của M1 — số event ở M1 chỉ đếm trên đầu ngón tay, và golden file ở C03 đã bắt được một phần. `NVM002` thì **không** cắt: nó chặn loại lỗi chỉ lộ ra ở site DE1 vào ngày đổi giờ.

---

### C17 — `test(architecture): add netarchtest rules for platform boundaries`

**Mục tiêu**: ranh giới ở `scope.md` §5.4 trở thành thứ **đỏ được**, thay vì một sơ đồ trong tài liệu.

**Việc làm**
- `tests/Architecture/Nvm.ArchitectureTests/` — `NetArchTest.Rules` **1.3.2** (netstandard2.0, chạy trên net10.0), xunit v3 theo đúng khuôn M0/C04.1.
- Rule tối thiểu:

| # | Rule | Ràng buộc |
|---|---|---|
| A1 | `Nvm.Contracts` không phụ thuộc bất kỳ package nào ngoài BCL | nền của K8 |
| A2 | `Nvm.Kernel` không phụ thuộc `MassTransit`, `EntityFrameworkCore`, `Npgsql`, `Microsoft.Data.SqlClient` | K9 |
| A3 | Functional Block không reference FB khác | K8 |
| A4 | Mọi type cài `IDomainEvent` không chứa `DateTime` | K2, phòng khi `NVM002` bị vô hiệu hoá |
| A5 | Mọi type cài `IDomainEvent` có `SiteId` | K3 |
| A6 | `Nvm.FactoryModel` không reference `Nvm.Bus` | FB phát event qua contract, không tự chọn transport |

**Kiểm chứng**
```bash
make test
```
Rồi cho mỗi rule ăn một vi phạm cố ý để xác nhận nó đỏ. **Không kiểm bước này thì rule chỉ là bốn dòng code luôn xanh.**

| Rule | Vi phạm cố ý | Kết quả |
|---|---|---|
| A1 | `Nvm.Contracts` dùng `IServiceCollection` (package ngoài BCL) | ✅ đỏ |
| A2 | `Nvm.Kernel` dùng `MassTransit.IBus` | ✅ đỏ |
| A3 | `Nvm.FactoryModel` reference `Nvm.Bus` | ✅ đỏ |
| A6 | (cùng vi phạm với A3) | ✅ đỏ, **test riêng** |
| A4 | Thêm `IReadOnlyList<DateTime>` vào event thật, **sau `#pragma warning disable NVM002`** | ✅ đỏ — xem C17.2 |
| A5 | Bỏ `[EventVersion(1)]`, **sau `#pragma warning disable NVM003`** | ✅ đỏ |
| — | Hoàn nguyên tất cả | ✅ **279** test xanh |

**Ghi chú**: A4 chồng lấn với `NVM002` (C16), có chủ đích. Analyzer chạy lúc build và có thể bị `#pragma warning disable`; architecture test chạy lúc test và duyệt IL của assembly đã build, nên `#pragma` không giấu được. Hai lớp bắt hai loại lách khác nhau.

#### C17.1 — `GetReferencedAssemblies()` KHÔNG phải danh sách `ProjectReference`

Control của A3 ban đầu viết là *"áp allowlist của FB lên `Nvm.Bus`, phải có thứ bị loại"* — vì `Nvm.Bus` có `ProjectReference` tới `Nvm.Hosting`. **Chạy thử: rỗng.**

Lý do: thứ duy nhất `Nvm.Bus` lấy từ `Nvm.Hosting` là `HealthTags.Ready`, một **`const`**. Const được nội tuyến lúc biên dịch, giá trị đi thẳng vào IL, và assembly kia **biến mất khỏi metadata**. `Nvm.Bus.GetReferencedAssemblies()` chỉ liệt kê `Nvm.Contracts`.

Đây là một phát hiện chứ không phải một lỗi cần sửa, và ranh giới đáng ghi rõ:

| Câu hỏi | Trả lời bằng |
|---|---|
| A có **gọi được** vào B không? | `GetReferencedAssemblies()` — đúng thứ K8/K9 quan tâm |
| Build của A có **phụ thuộc** B không? | file `.csproj` |

Rule của C17 hỏi câu thứ nhất, nên cách đọc hiện tại là đúng: một dependency mà compiler đã xoá thì không gọi được. Nếu đổi sang đọc `.csproj` thì một FB mượn đúng một hằng số sẽ bị báo vi phạm.

Control được đổi sang áp allowlist lên **chính assembly test** — nó reference `Nvm.Bus` và `Nvm.FactoryModel` và dùng thật cả hai.

#### C17.2 — Đã chạy thử `#pragma`, và đó là bằng chứng đáng giá nhất của C17

Ghi chú của plan nói analyzer *có thể* bị `#pragma warning disable`. Không suy luận, chạy thật:

```
#pragma warning disable NVM002   ở đầu FactoryModelRevisionActivated.cs
+ thêm  public IReadOnlyList<DateTime> Sneaky { get; init; } = [];

dotnet build Nvm.Contracts  → số lỗi NVM002: 0        ← analyzer đã bị bịt miệng
dotnet test                 → A4_NoEventCarriesADateTimeAnywhereInItsShape: ĐỎ
```

Lặp lại y hệt với `NVM003` + bỏ `[EventVersion(1)]`: build 0 lỗi, `A5_EveryEventDeclaresItsWireNameAndVersion` đỏ.

Một dòng `#pragma` là tất cả những gì cần để tắt một analyzer, và nó nằm trong chính file đang vi phạm — chỗ mà người review sẽ đọc lướt qua. Lớp thứ hai đọc metadata của assembly **đã sinh ra**, nơi `#pragma` không để lại dấu vết nào: property hoặc mang kiểu `DateTime`, hoặc không.

#### C17.3 — Mỗi rule có một "đối chứng dương" nằm lại trong bộ test

Vi phạm cố ý ở bảng trên chạy một lần rồi hoàn nguyên. Thứ **ở lại** là 6 test `*_Control_*`: mỗi cái áp đúng phép kiểm của rule lên một đối tượng **phải** bị bắt.

Lý do: một rule kiểm nhầm chỗ — danh sách type rỗng, tiền tố namespace gõ sai, `GetReferencedAssemblies` trả về rỗng — sẽ báo *"không tìm thấy vi phạm nào"* và xanh mãi mãi. Đúng bài học C15.1 (analyzer im lặng), chuyển sang tầng test.

Ví dụ rõ nhất là `ThereAreEventsToCheckAtAll`: nếu bộ lọc event hỏng thì **mọi** assertion "tất cả event đều ổn" ở A4 và A5 đều đúng một cách vô nghĩa.

---

### C18 — `docs: add event catalog and update oef mapping`

**Mục tiêu**: hai tài liệu sống bắt đầu có nội dung thật.

**Việc làm**
- **`docs/event-catalog.md`** (mới, `AGENTS.md` §6 đã giữ chỗ): bảng lấy từ `scope.md` §6.5, thêm cột `Version hiện tại`, `Đã cài đặt`, `Golden file`. Ở M1 chỉ có **một** dòng `Đã cài đặt = có` (`FactoryModelRevisionActivated` v1) và đó là con số đúng — bảng này đo tiến độ thật, không phải để trông cho đầy.
- **`docs/oef-mapping.md`** — cập nhật 6 dòng của M1:

| Khái niệm | Trạng thái sau M1 | Điều kiện |
|---|---|---|
| Bus-Centric Design | `xong` | dựng được **và** giải thích được vì sao bus là điều kiện để N15 khả thi |
| Manufacturing Service Bus | `xong` | topology + retry + DLQ chạy được, có số của lab phá hoại |
| Event-Driven Architecture | `đang làm` | envelope có, nhưng chưa có event store và chưa có upcaster → chưa đủ nói là hiểu |
| Functional Block | `đang làm` | `FactoryModel` là FB đầu tiên, nhưng chưa có `Facets/` và `Migrations/` — bố cục đầy đủ ở M5 |
| Factory Model | `đang làm` | cây ISA-95 và `equipment_path` chạy được, nhưng nguồn còn là seed file — persistence ở M5 (§3.1) |
| Command Handler | `đang làm` | pipeline có 3/4 behavior; `TransactionBehavior` ở M5 |
- Sửa cột *Ghi chú* của dòng *Event-Driven Architecture* (đang ghi "M6") và cột *Ra ở* của `ADR-004`/`ADR-008` trong `docs/adr/README.md` → **M1**. Theo §2.3.

**Kiểm chứng — đây là D5**

Không có lệnh nào kiểm được vế thứ hai của `xong`. Phép kiểm là tự trả lời **thành lời**, không đọc lại tài liệu:

1. Vì sao Opcenter chọn bus-centric thay vì cho các module gọi API của nhau?
2. Ràng buộc N15 sẽ vỡ ở chỗ nào nếu `Nvm.Ingestion` gọi HTTP đồng bộ sang `Nvm.App.Execution`? (K12)
3. `_error` queue giải quyết vấn đề gì mà retry không giải quyết được?

Trả lời lúng túng câu nào → dòng tương ứng **chưa** phải `xong`. Chính file `oef-mapping.md` đã đặt ra luật hai vế này; hạ trạng thái xuống `đang làm` không phải thất bại, ghi `xong` khi chưa đạt mới là.

---

### C19 — `docs: close m1 with benchmarks and checklist`

**Mục tiêu**: đóng milestone bằng số, không bằng cảm giác.

**Việc làm**
- `docs/benchmarks.md` — mục `## M1 — Factory Model & Service Bus`, tối thiểu 5 dòng số thật:
  - thời gian từ publish tới khi cả 2 consumer nhận xong (D1);
  - số lần thử trước khi vào `_error` (D2);
  - số message mất trong lab phá hoại (D4) — **con số quan trọng nhất của M1**;
  - thời gian `/health/ready` với 6 check, so với 451 ms / 10,4 ms của M0;
  - thời gian `make test` sau khi thêm ~3 project test.
- Cập nhật cột M1 trong `scope.md` Phụ lục A.
- Đổi `status: planned` → `status: done` trong frontmatter của plan này.
- Điền checklist §7.
- Nếu §2.4 đúng — không đo lại N13 — thì ghi một dòng trong mục *"Chỉ số cố ý KHÔNG đo"* của `benchmarks.md` nêu rõ lý do, để lần sau không tưởng là quên.

**Kiểm chứng**: đọc lại `benchmarks.md` và tìm ô nào ghi ước lượng thay vì số đo. Có một ô như vậy thì M1 chưa đóng được (`AGENTS.md` §1.3).

---

## 6. Rủi ro riêng của M1

| # | Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|---|
| R-M1-1 | Analyzer im lặng không chạy, và không ai biết | `make build` xanh cả sau khi cố tình vi phạm | Bảng 5 bước ở C15 là bắt buộc, không phải tuỳ chọn. Kiểm `AD0001` trong log build |
| R-M1-2 | M1 phình thành M5 | Bắt đầu viết migration, `DbContext`, hoặc `ProductionUnit` | Câu hỏi chặn ở §1: *thứ này có cần database không?* Có → hoãn |
| R-M1-3 | Lab phá hoại bị bỏ vì "biết trước kết quả rồi" | C13 xong mà `benchmarks.md` không có dòng số message mất | `AGENTS.md` §5.7: lab không có số thì chưa xong. Và con số đó là đầu vào của ADR outbox ở M6 |
| R-M1-4 | Topology đặt sai, phát hiện ở M2 | Queue tên theo tên class C#, hoặc classic queue thay vì quorum | C10.1 và C10.2. Đổi loại queue sau này phải xoá và tạo lại |
| R-M1-5 | Hai consumer dùng chung một queue, tưởng là fan-out | `make bus-fanout` in 1 dòng thay vì 2, hoặc in 2 dòng luân phiên qua các lần chạy | Phép kiểm thứ ba của D1: tắt một consumer, queue của nó phải **tăng** message |
| R-M1-6 | `ce_id` và `IdempotencyKey` lệch nhau | Không có dấu hiệu ở M1 — chỉ lộ ở M2 khi dedup đếm sai | C12 kiểm tường minh hai giá trị bằng nhau, và có test |
| R-M1-7 | Retry in-memory với khoảng cách quá dài làm nghẽn endpoint | Consumer đứng im trong lúc chờ retry | C11.2. Giữ khoảng cách ở mức giây. Redelivery dài là việc của M7 |
| R-M1-8 | Hết 1,5 tuần vẫn chưa xong | Đang ở ngày thứ 7 mà mới tới C11 | Cắt theo thứ tự: `NVM003` (C16) → `UseKillSwitch` (C11) → gộp C12 vào M6. **Không cắt** C13 và C17 — đó là DoD và là lớp ép quy ước |

---

## 7. Checklist M1

Đánh dấu khi commit đã vào `main`.

| # | Commit | ☐ | Ngày | Ghi chú |
|---|---|---|---|---|
| C01 | domain event marker + event version attribute | ☐ | | |
| C02 | cloudevents envelope + event type naming | ☐ | | |
| C03 | golden file + source-generated json context | ☐ | | |
| C04 | icommand, icommandhandler, dispatcher | ☐ | | ADR-010 |
| C05 | validation, idempotency, audit behaviors | ☐ | | |
| C06 | isa-95 node model + invariants | ☐ | | |
| C07 | seed NV1 + DE1 với equipment path index | ☐ | | |
| C08 | command activate model revision | ☐ | | |
| C09 | pin masstransit 8 + nvm.bus project | ☐ | | ADR-004, ADR-021 |
| C10 | exchange, routing key, queue topology | ☐ | | |
| C11 | retry, redelivery, dead letter policy | ☐ | | |
| C12 | cloudevents attributes qua transport header | ☐ | | ADR-008 |
| C13 | bus probe worker + 2 consumer độc lập | ☐ | | D1, D2, D4. ADR-022 |
| C14 | bus health check không phá startup | ☐ | | |
| C15 | analyzer NVM001 | ☐ | | D3 |
| C16 | analyzer NVM002 + NVM003 | ☐ | | |
| C17 | netarchtest cho ranh giới platform | ☐ | | |
| C18 | event catalog + oef mapping | ☐ | | D5 |
| C19 | benchmarks + đóng M1 | ☐ | | |

**Definition of Done**

| # | Tiêu chí | ☐ | Bằng chứng |
|---|---|---|---|
| ★ D1 | 1 publish → 2 consumer, 2 queue riêng | ☐ | |
| D2 | 5 lần thử → `_error` queue, không mất | ☐ | |
| D3 | `DateTime.UtcNow` làm build FAILED | ☐ | |
| D4 | Tắt RabbitMQ: không crash, số mất được đếm | ☐ | |
| D5 | 6 dòng OEF đúng trạng thái, ≥ 2 dòng `xong` | ☐ | |

**Sản phẩm phụ bắt buộc**

- [ ] `make test` xanh, số test > 22
- [ ] `tests/Architecture` có ≥ 5 rule, mỗi rule đã được chứng minh là đỏ được
- [ ] `ADR-004`, `ADR-008`, `ADR-010`, `ADR-021`, `ADR-022` viết xong — mỗi cái trong commit ra quyết định, không dồn về C18/C19
- [ ] `docs/event-catalog.md` tồn tại, chỉ ghi event **đã cài đặt thật**
- [ ] `docs/benchmarks.md` có ≥ 5 dòng số thật cho M1
- [x] `scope.md` §9/M1 và §9/M6 đã cập nhật theo §3.3 và C11.1 *(làm trước, 2026-08-26)*
- [ ] `docs/adr/README.md` (cột *Ra ở* của ADR-004, ADR-008) và `docs/oef-mapping.md` cập nhật theo §2.3 — làm ở C18

---

## 8. Sau M1

M2 (*Simulator, Ingestion & Idempotency*) là milestone kỹ thuật nặng nhất — 2,5 tuần, và `scope.md` nói thẳng *"đừng vội"*. Nó tiêu thụ gần như mọi thứ M1 dựng: `IdempotencyKey` của C04 thành khoá dedup thật, envelope của C02 thành canonical event, topology của C10 thành đường đi của 5.000 msg/s, và `EquipmentPath` của C06 thành MQTT topic.

**Ba thứ M1 để lại cho M2 phải kiểm trước khi bắt đầu**:

1. Con số message mất ở C13.3 — nó quyết định outbox được kéo về sớm (M2) hay giữ ở M6.
2. `ce_id == IdempotencyKey` (C12) — nếu lệch thì dedup hai tầng của §7.2 không nối được vào nhau.
3. `Nvm.BusProbe` — M2 có `EdgeGateway` và `Ingestion` thật. Lúc đó probe worker hết vai trò: xoá nó, đừng để lại như một thư mục không ai chạy.

Đọc `scope.md` §7.1 (Sparkplug B), §7.2 (idempotency) và §9/M2 trước khi lập plan M2.
