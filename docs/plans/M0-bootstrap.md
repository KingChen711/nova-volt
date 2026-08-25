---
title: "M0 — Bootstrap & Walking Skeleton"
milestone: M0
duration: "1 tuần (~12 giờ)"
status: not-started
created: 2026-08-25
depends_on: []
unlocks: [M1]
---

# M0 — Bootstrap & Walking Skeleton

> **Mục tiêu**: có một đường đi xuyên suốt từ hạ tầng tới UI, dù nó chưa làm gì cả.
>
> Đọc trước: [`AGENTS.md`](../../AGENTS.md) · [`scope.md` §5.4 (repo), §5.5 (persistence), §9/M0](../scope.md)

---

## 1. Definition of Done

Milestone chỉ được đóng khi **cả 5** mệnh đề đúng, có bằng chứng chạy được:

| # | Tiêu chí | Cách chứng minh |
|---|---|---|
| ★ D1 | Máy sạch → `make up` → tất cả health check xanh trong **< 5 phút** | `make up` in ra thời gian; chụp lại |
| D2 | `make test` chạy được và xanh | Output test runner |
| D3 | Mendix app đăng nhập bằng Keycloak, hiện tên + role của user | Ảnh chụp màn hình |
| D4 | `make ci` xanh, và pre-commit hook chặn được code sai format | Output terminal |
| D5 | Tắt SQL Server → `/health/ready` chuyển `Unhealthy` trong < 10 s, app **không** crash | Log + curl (Lab phá hoại) |

**Không** thuộc M0: event store, Functional Block, bus topology, domain logic. M0 chỉ dựng khung.

---

## 2. Phát hiện từ máy thật — ba điều chỉnh so với `scope.md`

Kiểm tra máy ngày 2026-08-25 cho ra ba điểm lệch. Xử lý theo [`AGENTS.md` §2.3](../../AGENTS.md).

### 2.1 Đổi .NET 9 → .NET 10 LTS *(đã cập nhật scope.md)*

| | |
|---|---|
| **Thực tế** | SDK cài trên máy là `10.0.300`. Không có SDK 9. |
| **Vấn đề** | .NET 9 là bản **STS**, vòng đời 18 tháng, đã hết hỗ trợ khoảng 5/2026. Dùng nó bây giờ là chọn một runtime không còn nhận bản vá bảo mật. |
| **Quyết định** | Dùng **.NET 10 LTS**. |
| **Đánh đổi** | Không có gì đáng kể — mọi thư viện trong scope đều hỗ trợ net10.0. `TimeProvider`, `FakeTimeProvider`, `Guid.CreateVersion7()` đều có sẵn. |
| **Việc phải làm** | Ghi `ADR-019: chọn .NET 10 LTS` ở C14. |

### 2.2 Chia `docker-compose` thành profile

| | |
|---|---|
| **Thực tế** | RAM 15,4 GB tổng, lúc kiểm tra còn trống 4,1 GB. |
| **Vấn đề** | Full stack (SQL Server 2 GB + Postgres + RabbitMQ + EMQX + MinIO + Keycloak + Grafana/Tempo/Loki/Prometheus/OTel) khoảng **6 GB**. Bật hết mỗi ngày sẽ làm máy không dùng được cho việc khác. |
| **Quyết định** | Chia profile. `make up` chỉ bật những gì M0–M12 cần (~4 GB). Observability stack vào profile `obs`, mặc định tắt, bật từ M13 hoặc khi cần debug. |
| **Ràng buộc thêm** | Mọi service phải khai báo `mem_limit` tường minh — để biết cái nào ngốn, và để container không nuốt hết RAM máy. |
| **Ảnh hưởng DoD** | D1 đo trên profile mặc định. Ghi riêng thời gian khởi động profile `obs` khi tới M13. |

| Profile | Service | RAM ước tính |
|---|---|---|
| *(mặc định)* | mssql, timescale, rabbitmq, emqx, minio, keycloak | ~4,0 GB |
| `obs` | otel-collector, prometheus, tempo, loki, grafana | ~1,5 GB |

### 2.3 Docker daemon chưa chạy

Không phải vấn đề thiết kế, chỉ là điều kiện tiên quyết: **bật Docker Desktop trước C05**, và trong Settings → Resources đặt Memory ≥ 8 GB (mặc định WSL2 thường lấy 50% RAM, tức ~7,7 GB — vừa đủ).

---

## 3. Quyết định đã chốt

Xác nhận ngày 2026-08-25:

| # | Vấn đề | Quyết định | Hệ quả lên plan |
|---|---|---|---|
| Q1 | Repo GitHub remote | **Chưa tạo.** Làm local trước | C12 đổi hướng: xây `make ci` chạy local + pre-commit hook, `ci.yml` viết sẵn nhưng chưa chạy. D4 đổi thành *"`make ci` xanh"*. Thêm `make backup` ở C11 |
| Q2 | Mendix Studio Pro | **11.12.1** | OIDC SSO yêu cầu Mendix 9.0+ → tương thích. Cần kèm **Community Commons** |
| Q3 | Mendix Cloud node | **Không dùng trong M0** | C13 chạy bằng `Run Locally`. Cloud node để tới M13 |

### 3.1 Vì sao đổi C12 thay vì hoãn nó

Một file `ci.yml` không bao giờ chạy là tài liệu, không phải người gác cổng. Nhưng **giá trị thật của CI không nằm ở GitHub** — nó nằm ở chỗ *có một bộ kiểm tra cố định, chạy được bằng một lệnh, không phụ thuộc vào việc bạn có nhớ hay không*.

Nên C12 xây `make ci` chạy đúng những bước mà GitHub sẽ chạy. Khi nào tạo remote, `ci.yml` chỉ việc gọi lại đúng các bước đó — không phải viết lại. Bạn được người gác cổng **ngay bây giờ**, và không mất gì khi thêm remote sau.

### 3.2 Một lưu ý về backup — không phải về CI

Không có remote thì CI chỉ là bất tiện nhỏ. Điều đáng lo hơn là **6 tháng công sức chỉ nằm trên ổ D của một máy**. Ổ hỏng, Windows lỗi, hoặc lỡ tay `git clean -fd` là mất sạch, kể cả lịch sử commit.

Cách rẻ nhất mà **không cần remote**: `git bundle` — nén toàn bộ repo kèm lịch sử thành một file, chép sang OneDrive.

```bash
git bundle create "$ONEDRIVE/backup/novavolt-mes.bundle" --all
```

Thêm thành target `make backup` ở C11, chạy cuối mỗi buổi làm việc. Khôi phục bằng `git clone novavolt-mes.bundle`.

Khi nào nên tạo repo GitHub private *(miễn phí, không giới hạn repo private)*: khi bắt đầu M5 — lúc đó code đã đủ nhiều để mất là tiếc, và CI thật bắt đầu có ích vì có integration test.

---

## 4. Tổng quan 16 commit

| # | Commit message | Giai đoạn | Ước lượng | Phụ thuộc |
|---|---|---|---|---|
| C01 | `chore: initialize repository skeleton` | A — Nền repo | 45' | — |
| C02 | `build: add central build and package configuration` | A | 45' | C01 |
| C03 | `feat(host): add minimal host with liveness endpoint` | A | 45' | C02 |
| C04 | `test: add unit test project and make test target` | A | 45' | C03 |
| C05 | `chore(infra): add docker networks and compose skeleton` | B — Hạ tầng | 30' | C01 |
| C06 | `chore(infra): add postgresql with timescaledb` | B | 45' | C05 |
| C07 | `chore(infra): add sql server` | B | 45' | C05 |
| C08 | `chore(infra): add rabbitmq and emqx` | B | 45' | C05 |
| C09 | `chore(infra): add minio and keycloak realm` | B | 75' | C05 |
| C10 | `feat(host): add readiness checks for all dependencies` | C — Health & DX | 60' | C06–C09 |
| C11 | `chore: complete makefile with up, down, backup` | C | 45' | C10 |
| C12 | `ci: add local ci pipeline and github workflow` | C | 45' | C04 |
| C13 | `feat(mendix): add NvmShopFloor app with keycloak sso` | D — Mendix | 60' | C09 |
| C14 | `docs: add adr template and first three records` | E — Đóng M0 | 60' | C02, C06, C07 |
| C15 | `docs: add oef-mapping and benchmarks skeleton` | E | 30' | — |
| C16 | `docs: finalize readme quickstart and close M0` | E | 45' | tất cả |

**Tổng: ~12 giờ.** Giai đoạn A và B độc lập nhau — làm A trước cho quen tay, hoặc B trước nếu muốn chờ image tải về trong lúc làm A.

> [!important] Nhắc lại từ AGENTS.md §1.1
> Agent **không tự commit**. Xong mỗi C, dừng lại, báo cáo, bạn tự đọc `git diff` rồi commit. Commit message ở cột trên là **đề xuất**, bạn sửa thoải mái.

---

## 5. Chi tiết từng commit

### C01 — `chore: initialize repository skeleton`

**Mục tiêu**: biến thư mục thành git repo, có đủ file cấu hình nền, có cây thư mục khớp `scope.md` §5.4.

**Việc làm**
- `git init -b main`
- `.gitignore` — .NET (`bin/`, `obj/`, `*.user`), Mendix (`deployment/`, `*.mpr.bak`, `modeler-cache/`), Docker volume, IDE (`.vs/`, `.idea/`), secrets (`*.env`, `appsettings.*.Local.json`)
- `.gitattributes` — **quan trọng trên Windows**:
  ```
  * text=auto eol=lf
  *.sh    text eol=lf
  *.ps1   text eol=crlf
  *.cmd   text eol=crlf
  *.mpr   binary
  *.mpk   binary
  *.png   binary
  ```
- `.editorconfig` — C# style, `dotnet_diagnostic.*.severity`, `csharp_style_namespace_declarations = file_scoped:error`, indent 4 spaces cho `.cs`, 2 cho `.yml`/`.json`
- `README.md` — khung: mục tiêu 3 dòng, quickstart (để trống, điền ở C16), cấu trúc thư mục, link `AGENTS.md` và `docs/scope.md`
- Tạo cây thư mục rỗng với `.gitkeep`:
  ```
  src/Platform  src/FunctionalBlocks  src/Apps  src/Workers
  tests/Unit  tests/Architecture  tests/Integration  tests/Contract  tests/Load  tests/Chaos
  tools/templates  tools/analyzers  tools/solution-cli
  deploy/helm  deploy/k3d
  mendix
  ```
- `docs/`, `AGENTS.md` đã có sẵn — chỉ cần đưa vào commit đầu tiên

**Kiểm chứng**
```bash
git status --short          # sạch sau khi add
git ls-files | wc -l        # ~25 file
```
Tạo một file `.sh` thử, `git add`, kiểm `git diff --cached` không có `^M`.

**Bẫy**: Windows mặc định `core.autocrlf=true` sẽ đánh nhau với `.gitattributes`. Đặt `git config core.autocrlf false` cho repo này.

---

### C02 — `build: add central build and package configuration`

**Mục tiêu**: mọi project sau này thừa hưởng cùng một bộ quy tắc build, không lặp lại `<Nullable>` ở 20 file `.csproj`.

**Việc làm**
- `global.json` — pin SDK:
  ```json
  { "sdk": { "version": "10.0.300", "rollForward": "latestFeature" } }
  ```
- `Directory.Build.props` (root):
  - `TargetFramework: net10.0`
  - `Nullable: enable`
  - `ImplicitUsings: enable`
  - `LangVersion: latest`
  - `TreatWarningsAsErrors: true`
  - `EnforceCodeStyleInBuild: true`
  - `AnalysisLevel: latest-recommended`
  - `GenerateDocumentationFile: true` (để CS1591 nhắc viết doc cho public API — cân nhắc tắt nếu quá ồn)
  - `InvariantGlobalization: true`
- `Directory.Packages.props` — `ManagePackageVersionsCentrally: true`, khai báo trước: Serilog.AspNetCore, Microsoft.Extensions.Hosting, xunit.v3, Shouldly
- `NovaVolt.Mes.sln`
- `Directory.Build.props` riêng trong `tests/` — tắt `TreatWarningsAsErrors` cho test project (test hay có warning vô hại)

**Kiểm chứng**
```bash
dotnet --version            # 10.0.300
dotnet build                # solution rỗng, Build succeeded, 0 warnings
```

**Quyết định cần ghi**: có bật `TreatWarningsAsErrors` không. Bật thì khó chịu lúc đầu nhưng giữ code sạch suốt 6 tháng. **Khuyến nghị: bật**, và ghi vào ADR nếu sau này phải tắt.

---

### C03 — `feat(host): add minimal host with liveness endpoint`

**Mục tiêu**: có một process .NET chạy được, có log, có endpoint sống.

**Việc làm**
- `src/Apps/Nvm.Host.All/Nvm.Host.All.csproj` (Web SDK, minimal API)
- `Program.cs`:
  - Serilog console sink, output template có timestamp ISO-8601 + level + message
  - `builder.Services.AddHealthChecks()`
  - `/health/live` → tag `live`, luôn trả 200 nếu process sống
  - `/health/ready` → tag `ready`, tạm thời chưa có check nào → trả `Healthy`
  - `/` → trả `{ name, version, environment, utcNow }`
- `appsettings.json` + `appsettings.Development.json`
- `Properties/launchSettings.json` — port cố định `5080` (http), không dùng port ngẫu nhiên

**Kiểm chứng**
```bash
dotnet run --project src/Apps/Nvm.Host.All
curl -s localhost:5080/health/live    # Healthy
curl -s localhost:5080/health/ready   # Healthy
curl -s localhost:5080/               # JSON có version
```

**Ghi chú thiết kế**: tách `live` và `ready` **ngay từ đầu**, không gộp thành một `/health`. Liveness = "process còn sống, đừng restart tôi". Readiness = "tôi sẵn sàng nhận traffic". Gộp hai cái là lỗi kinh điển làm Kubernetes restart pod chỉ vì DB tạm chậm.

---

### C04 — `test: add unit test project and make test target`

**Mục tiêu**: có khung test và một lệnh duy nhất để chạy.

**Việc làm**
- `tests/Unit/Nvm.UnitTests/` — xUnit v3 + Shouldly
- **Một test thật**, không phải `Assert.True(true)`. Gợi ý: test một helper nhỏ có ý nghĩa, ví dụ `SerialNumber.Parse` tách được site/type/line/năm/ngày/ca/seq từ chuỗi 16 ký tự (§6.1 scope). Viết luôn cả case chuỗi sai định dạng.
- `Makefile` với target `test`:
  ```make
  test: ; dotnet test --nologo --verbosity minimal
  ```
- Thêm project vào `.sln`

**Kiểm chứng**
```bash
make test     # 2 passed
```
Sửa cố ý cho test đỏ → `make test` phải fail → hoàn nguyên.

**Ghi chú**: dùng **Shouldly** chứ không FluentAssertions — FluentAssertions từ v8 đổi sang license thương mại. Chi tiết ở `scope.md` §10.1.

**Bẫy Windows**: `make` không có sẵn. Hai lựa chọn — cài `make` qua `winget install GnuWin32.Make` / Chocolatey, hoặc thay bằng `build.ps1`. Nếu chọn PowerShell, sửa mọi chỗ nhắc `make` trong plan này và ghi một dòng vào README.

---

### C05 — `chore(infra): add docker networks and compose skeleton`

**Mục tiêu**: dựng ranh giới mạng OT/DMZ/IT trước khi có service nào — để không bao giờ phải "sửa lại cho đúng" sau.

**Việc làm**
- `docker-compose.yml`:
  ```yaml
  name: novavolt-mes
  networks:
    ot-net:  { internal: true }
    dmz-net: {}
    it-net:  {}
  ```
- Một service tạm `netcheck` (alpine, `sleep infinity`) gắn vào `it-net` để test
- `.env.example` — mọi biến môi trường, giá trị mặc định dev, **không** có secret thật
- `.env` vào `.gitignore`

**Kiểm chứng**
```bash
docker compose up -d
docker network ls | grep novavolt          # 3 network
docker network inspect novavolt-mes_ot-net # "Internal": true
```

**Ghi chú**: `internal: true` nghĩa là container trong `ot-net` **không ra được internet và không tới được network khác**. Đây chính là K11 trong `AGENTS.md`. Test kiểm chứng thật sẽ viết ở M2 khi đã có simulator.

---

### C06 — `chore(infra): add postgresql with timescaledb`

**Mục tiêu**: store cho telemetry và read model.

**Việc làm**
- Service `timescale`, image `timescale/timescaledb-ha:pg17` *(kiểm tra tag còn tồn tại; nếu không, dùng `timescale/timescaledb:latest-pg17`)*
- Network: `it-net`. Port 5432 map ra ngoài để dùng psql/DBeaver
- Named volume `pgdata`
- `mem_limit: 1g`
- `healthcheck: pg_isready -U nvm`
- Init script `deploy/postgres/init/01-schemas.sql`:
  ```sql
  CREATE EXTENSION IF NOT EXISTS timescaledb;
  CREATE SCHEMA IF NOT EXISTS ts;      -- telemetry
  CREATE SCHEMA IF NOT EXISTS rm;      -- read model
  CREATE SCHEMA IF NOT EXISTS trace;   -- genealogy
  CREATE SCHEMA IF NOT EXISTS ingest;  -- dedup
  ```

**Kiểm chứng**
```bash
docker compose up -d timescale
docker compose exec timescale psql -U nvm -d novavolt \
  -c "SELECT extversion FROM pg_extension WHERE extname='timescaledb';"
docker compose exec timescale psql -U nvm -d novavolt -c "\dn"   # 4 schema
```

---

### C07 — `chore(infra): add sql server`

**Mục tiêu**: store cho event store và write model — chọn có chủ đích để bám sát Opcenter thật.

**Việc làm**
- Service `mssql`, image `mcr.microsoft.com/mssql/server:2022-latest`
- `ACCEPT_EULA=Y`, `MSSQL_PID=Developer`, `SA_PASSWORD` từ `.env`
- Network `it-net`, port 1433, volume `mssqldata`
- `mem_limit: 2g` (SQL Server cần tối thiểu 2 GB, dưới mức đó nó **từ chối khởi động**)
- `healthcheck` bằng `sqlcmd -Q "SELECT 1"` — lưu ý image 2022 dùng đường dẫn `/opt/mssql-tools18/bin/sqlcmd` và cần cờ `-C` (trust cert)
- Init script `deploy/mssql/init/01-database.sql`: tạo DB `NovaVolt`, schema `es`, login `nvm_app` + user
- Vì SQL Server image không tự chạy init script như Postgres → thêm service phụ `mssql-init` chạy một lần rồi thoát, hoặc script `deploy/mssql/entrypoint.sh`

**Kiểm chứng**
```bash
docker compose up -d mssql
docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C \
  -Q "SELECT name FROM sys.databases WHERE name='NovaVolt'"
```

**Bẫy đã biết**: SQL Server là service ngốn RAM nhất và khởi động chậm nhất (~30–45 s). Đây gần như chắc chắn là đường găng của D1 (< 5 phút). Đo riêng thời gian của nó ở C11.

---

### C08 — `chore(infra): add rabbitmq and emqx`

**Mục tiêu**: Manufacturing Service Bus và MQTT broker.

**Việc làm**
- `rabbitmq:4-management` — network `it-net`, port 5672 + 15672 (management UI), volume, `mem_limit: 512m`, healthcheck `rabbitmq-diagnostics -q ping`
- `emqx/emqx:5.8` — network **`ot-net` và `dmz-net`** (đây là điểm quan trọng: broker là cầu nối giữa OT và DMZ), port 1883 + 18083 (dashboard), `mem_limit: 512m`, healthcheck `emqx ctl status`

**Kiểm chứng**
```bash
curl -u guest:guest localhost:15672/api/overview   # JSON
curl localhost:18083                                # EMQX dashboard
# pub/sub thử bằng mosquitto_clients hoặc EMQX WebSocket client trên dashboard
```

**Ghi chú kiến trúc**: EMQX cố ý **không** nằm trên `it-net`. Service .NET muốn nghe MQTT phải qua `Nvm.EdgeGateway` ở `dmz-net` (M2). Nếu bây giờ bạn thấy bất tiện, đó là dấu hiệu ranh giới đang hoạt động đúng.

---

### C09 — `chore(infra): add minio and keycloak realm`

**Mục tiêu**: object storage cho evidence, và identity provider cho cả .NET lẫn Mendix.

**Việc làm**
- `minio/minio` — network `it-net`, port 9000 + 9001 (console), volume, `mem_limit: 512m`
- Service phụ `minio-init` dùng `minio/mc` tạo bucket `evidence`, `vision`, rồi thoát
- `quay.io/keycloak/keycloak:26.x` — `start-dev`, network `it-net`, port 8080, `mem_limit: 768m`
- `deploy/keycloak/realm-novavolt.json` — realm export chứa:
  - Realm `novavolt`
  - Client `nvm-api` (confidential, service account) và `nvm-mendix` (public hoặc confidential tuỳ module OIDC của Mendix)
  - Realm role: `Operator`, `LineLeader`, `QaEngineer`, `QaManager`, `ProductionManager`, `ComplianceOwner`, `Admin`
  - User attribute `site_id`, thêm vào token qua protocol mapper
  - 3 user test: `op.nv1` (Operator, site NV1), `qa.nv1` (QaEngineer, NV1), `op.de1` (Operator, **site DE1** — để test cross-site isolation ở M10)
- Import realm bằng `--import-realm` + mount file vào `/opt/keycloak/data/import/`

**Kiểm chứng**
```bash
# Lấy token và đọc claim
curl -s -d "client_id=nvm-mendix" -d "username=op.nv1" -d "password=..." \
     -d "grant_type=password" \
     localhost:8080/realms/novavolt/protocol/openid-connect/token | jq -r .access_token
# Decode payload → phải thấy realm_access.roles chứa Operator, và site_id = NV1
```

**Đây là commit tốn thời gian nhất của M0** (75'). Realm export dễ sai ở protocol mapper. Nếu bí, tạo realm bằng UI trước rồi export ra file, đừng viết JSON bằng tay.

---

### C10 — `feat(host): add readiness checks for all dependencies`

**Mục tiêu**: `/health/ready` phản ánh **đúng** tình trạng thật của toàn hệ thống.

**Việc làm**
- Thêm package: `AspNetCore.HealthChecks.SqlServer`, `.NpgSql`, `.Rabbitmq`, `.Uris` (cho Keycloak), `.Aws.S3` hoặc custom cho MinIO
- Health check cho EMQX: custom, thử TCP connect port 1883 (hoặc gọi API dashboard)
- Mỗi check gắn tag `ready` + tên riêng, `timeout: 3s`
- `/health/ready` trả JSON chi tiết: tên check, status, duration, exception message (chỉ ở Development)
- Connection string đọc từ `.env` qua environment variable, **không** hardcode

**Kiểm chứng** — đây cũng là **Lab phá hoại của M0**:
```bash
curl -s localhost:5080/health/ready | jq          # tất cả Healthy
docker compose stop mssql
# đợi < 10 s
curl -s localhost:5080/health/ready | jq          # sqlserver = Unhealthy, các check khác vẫn Healthy
curl -s localhost:5080/health/live                # VẪN Healthy — process không chết
docker compose start mssql
# đợi ~40 s
curl -s localhost:5080/health/ready | jq          # trở lại Healthy, không cần restart app
```

**Đây là D5.** Ghi lại thời gian phát hiện và thời gian tự phục hồi vào `docs/benchmarks.md`.

> [!warning] Đừng để app crash khi dependency chết
> Nếu `Program.cs` mở connection lúc startup và ném exception, app sẽ không lên nổi khi DB chưa sẵn sàng — vi phạm tinh thần N15. Health check phải **lazy**, kiểm tra lúc được gọi, không phải lúc khởi động.

---

### C11 — `chore: complete makefile with up, down, backup`

**Mục tiêu**: một lệnh duy nhất, và một con số cho D1.

**Việc làm**
- Target: `up`, `up-obs`, `down`, `down-v` (xoá volume), `logs`, `ps`, `test`, `format`, `clean`, `reset`, `backup`
- `backup` — git bundle sang OneDrive, xem §3.2. Chạy cuối mỗi buổi làm việc:
  ```make
  backup: ; git bundle create "$(BACKUP_DIR)/novavolt-mes.bundle" --all \
           && echo "Backed up to $(BACKUP_DIR)"
  ```
  `BACKUP_DIR` đọc từ `.env`, mặc định trỏ vào thư mục OneDrive
- `up` phải **chờ tất cả healthy rồi mới trả về**, và in ra thời gian đã trôi:
  ```make
  up:
  	@start=$$(date +%s); \
  	docker compose up -d --wait; \
  	echo "All healthy in $$(($$(date +%s)-start))s"
  ```
  (`docker compose up --wait` chờ healthcheck — dùng nó thay vì viết vòng lặp thủ công)
- `up-obs` = `docker compose --profile obs up -d --wait`
- `format` = `dotnet format` ; `format-check` = `dotnet format --verify-no-changes`

**Kiểm chứng — đây là D1** ★
```bash
make down-v          # xoá sạch volume
docker system prune  # tuỳ chọn: mô phỏng máy sạch hơn nữa
make up              # phải in ra "All healthy in <300s"
```

Chạy **3 lần** và ghi cả ba con số vào `docs/benchmarks.md` (lần đầu chậm hơn vì pull image — ghi rõ lần nào là cold).

**Nếu quá 5 phút**: nghi phạm số một là SQL Server. Cách xử lý theo thứ tự — tăng `mem_limit` lên 2,5 g; giảm `healthcheck.interval`; nới `start_period`. Nếu vẫn không đạt, **đó là lúc đề xuất sửa DoD**, không phải lúc gian lận bằng cách bỏ bớt service. Theo quy trình `AGENTS.md` §3.2.

---

### C12 — `ci: add local ci pipeline and github workflow`

**Mục tiêu**: có người gác cổng chạy được **ngay hôm nay**, và sẵn sàng chuyển lên GitHub khi cần.

**Việc làm**
- Target `make ci` — đúng thứ tự và đúng cờ mà CI sẽ dùng, không phải một biến thể khác:
  ```make
  ci: ; dotnet restore \
      && dotnet format --verify-no-changes \
      && dotnet build -c Release --no-restore \
      && dotnet test  -c Release --no-build --nologo
  ```
- `.githooks/pre-commit` — **chỉ** chạy `dotnet format --verify-no-changes` (< 5 s). Cố ý **không** chạy build/test trong hook: hook chậm thì bạn sẽ tìm cách `--no-verify`, và thế là mất luôn tác dụng
- Kích hoạt hook: `git config core.hooksPath .githooks` — ghi vào README, vì hook **không** tự cài khi clone
- `.github/workflows/ci.yml` viết sẵn, trigger `push` + `pull_request`, các bước khớp `make ci`: `actions/checkout` → `actions/setup-dotnet` (đọc `global.json`) → cache NuGet → 4 bước như trên. `permissions: contents: read`
- Chưa thêm Testcontainers — integration test đến ở M5, khi đó tách job riêng

**Kiểm chứng — đây là D4 (phiên bản không cần remote)**
```bash
make ci                       # xanh
# Cố tình phá format 1 file (thụt lề sai), rồi:
make ci                       # ĐỎ ở bước format
git add . && git commit -m "test"   # pre-commit hook CHẶN
make format                   # sửa lại
make ci                       # xanh trở lại
```

**Bẫy**: `dotnet format --verify-no-changes` gần như chắc chắn đỏ ở lần chạy đầu, vì `.editorconfig` ở C01 chặt hơn code do template sinh ra. Chạy `make format` một lần rồi hẵng bật hook.

**Khi có remote sau này**: không phải viết lại gì — `ci.yml` đã sẵn, chỉ cần `git remote add` và push.

---

### C13 — `feat(mendix): add NvmShopFloor app with keycloak sso`

**Mục tiêu**: chứng minh đường đi từ Mendix tới identity — nền cho mọi màn hình sau này.

> [!important] Mendix có repo riêng
> App Mendix sống trong **Team Server repo của chính nó**, không nằm trong repo `novavolt-mes`. Trong repo chính, commit này chỉ thêm `mendix/README.md`. Công việc mô hình hoá được commit riêng trong Team Server.

**Việc làm — phía Mendix Studio Pro 11.12.1**
- Tạo app `NvmShopFloor` từ Blank template
- Cài module **OIDC SSO** từ Marketplace *(yêu cầu Mendix 9.0+, nên 11.12.1 dùng được)* kèm dependency bắt buộc **Community Commons**. Bản OIDC ≤ 4.3.0 cần thêm **Encryption** — kiểm tra version lúc cài, đừng cài thừa
- Cấu hình OIDC trỏ tới Keycloak realm `novavolt`, client `nvm-mendix`
- **Chạy bằng `Run Locally`** — không cần Mendix Cloud node ở milestone này (xem §3, Q3)
- Tạo module `NvmShared` (chuẩn bị cho C-về-sau: connector POM, response mapper)
- Một trang `Home_Web`: hiện `Tên user`, `Email`, danh sách role, `site_id`
- Module role `Operator`, `LineLeader` map từ realm role
- `mx check` sạch

**Việc làm — phía repo chính**
- `mendix/README.md`: tên app, URL Team Server, phiên bản Studio Pro, các module Marketplace đã dùng + version, cách chạy local, ghi chú "không commit `.mpr` dirty"

**Kiểm chứng — đây là D3**
1. `Run Locally` trong Studio Pro
2. Mở trình duyệt → bị chuyển sang trang login Keycloak
3. Đăng nhập bằng `op.nv1`
4. Quay lại app, thấy tên user + role `Operator` + `site_id = NV1`
5. Chụp màn hình lưu vào `docs/evidence/M0-D3-mendix-login.png`

**Bẫy đã biết**: Keycloak chạy trong Docker ở `localhost:8080`, còn Mendix chạy ở `localhost:8080` mặc định → **đụng port**. Đổi Mendix sang `8090`, hoặc đổi Keycloak sang `8081`. Quyết định sớm, vì đổi sau phải sửa cả realm config.

---

### C14 — `docs: add adr template and first three records`

**Mục tiêu**: bắt đầu thói quen ghi ADR **trước** khi cần nó — vì tới tháng thứ 4 bạn sẽ không nhớ vì sao chọn gì.

**Việc làm**
- `docs/adr/_template.md` — Context / Decision / Consequences / Alternatives considered / Status / Date
- `docs/adr/README.md` — bảng index, cập nhật mỗi lần thêm ADR
- **ADR-001** — *Chọn SQL Server cho event store và write model*: bám sát stack Opcenter thật (kiến trúc tham chiếu AWS xác nhận SQL Server là primary data store); đánh đổi là mất range type và `EXCLUDE` constraint, nên read model phải ở PostgreSQL
- **ADR-002** — *Chọn PostgreSQL + TimescaleDB cho telemetry và read model*: cần `numrange` + GiST cho trace theo mét, cần hypertable/compression/continuous aggregate; hệ quả là phải chấp nhận polyglot persistence và projection không cùng transaction với write
- **ADR-019** — *Chọn .NET 10 LTS thay vì .NET 9*: theo §2.1 của plan này

**Kiểm chứng**: đọc lại ADR-002 và tự hỏi *"3 tháng nữa đọc cái này, tôi có hiểu vì sao không?"*. Nếu không → viết lại phần Context.

---

### C15 — `docs: add oef-mapping and benchmarks skeleton`

**Mục tiêu**: hai tài liệu sống, cập nhật suốt 26 tuần.

**Việc làm**
- `docs/oef-mapping.md` — copy bảng từ `scope.md` §5.2, thêm cột **`Trạng thái`** (`chưa làm` / `đang làm` / `xong` / `khác scope`) và cột **`Bài trong course`**. Cập nhật mỗi khi học xong một video Opcenter hoặc làm xong một thành phần
- `docs/benchmarks.md` — bảng rỗng theo mẫu `scope.md` Phụ lục A, cột: `Ngày · Commit · Chỉ số · Giá trị · Điều kiện đo`. Điền ngay 2 dòng đầu từ C10 (thời gian phát hiện Unhealthy) và C11 (thời gian `make up` × 3 lần)

**Kiểm chứng**: `docs/benchmarks.md` có ít nhất 4 dòng số thật, không có ô nào ghi ước lượng.

---

### C16 — `docs: finalize readme quickstart and close M0`

**Mục tiêu**: một dev lạ chạy được trong 15 phút.

**Việc làm**
- README quickstart: prerequisites (Docker Desktop ≥ 8 GB, .NET 10 SDK, make, Mendix Studio Pro), `cp .env.example .env`, `make up`, `make test`, `dotnet run`, URL của từng UI (RabbitMQ 15672, EMQX 18083, MinIO 9001, Keycloak 8081)
- Bảng port ở một chỗ duy nhất
- Cập nhật checklist M0 trong `scope.md` Phụ lục A
- Cập nhật `status: done` trong frontmatter của plan này

**Kiểm chứng — thật sự làm, đừng bỏ qua**
1. `make down-v && docker system prune -f`
2. Mở README trong cửa sổ khác, **làm theo từng dòng như người chưa biết gì**
3. Bấm giờ. Nếu quá 15 phút hoặc phải tra cứu ngoài README → README chưa xong

---

## 6. Rủi ro riêng của M0

| # | Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|---|
| R-M0-1 | `make up` quá 5 phút vì SQL Server | C11 đo được 5–7 phút | Tăng `mem_limit`, nới `start_period`. Vẫn không đạt → đề xuất sửa DoD theo AGENTS.md §3.2 |
| R-M0-2 | Máy hết RAM khi bật hết stack | Docker Desktop báo OOM, container bị kill | Đã né bằng profile (§2.2). Nếu vẫn thiếu → tắt Keycloak khi không làm Mendix |
| R-M0-3 | Không có `make` trên Windows | C04 fail | Cài GnuWin32 Make, hoặc chuyển sang `build.ps1` và sửa plan |
| R-M0-4 | Realm export Keycloak sai mapper | Token không có `site_id` | Tạo bằng UI rồi export, đừng viết JSON tay |
| R-M0-5 | Đụng port Mendix ↔ Keycloak (cùng 8080) | C13 không chạy được | Quyết định port ở C09, ghi vào bảng port trong README |
| R-M0-6 | Sa đà vào hạ tầng, hết tuần vẫn chưa xong M0 | Đang ở ngày thứ 6 mà mới tới C08 | M0 là **khung**, không phải sản phẩm. Cắt: bỏ MinIO (dời sang M3), bỏ observability. Ghi lại đã cắt gì |

---

## 7. Checklist M0

Đánh dấu khi commit đã vào `main`.

| # | Commit | ☐ | Ngày | Ghi chú |
|---|---|---|---|---|
| C01 | initialize repository skeleton | ☐ | | |
| C02 | central build and package configuration | ☐ | | |
| C03 | minimal host with liveness endpoint | ☐ | | |
| C04 | unit test project and make test target | ☐ | | |
| C05 | docker networks and compose skeleton | ☐ | | |
| C06 | postgresql with timescaledb | ☐ | | |
| C07 | sql server | ☐ | | |
| C08 | rabbitmq and emqx | ☐ | | |
| C09 | minio and keycloak realm | ☐ | | |
| C10 | readiness checks for all dependencies | ☐ | | |
| C11 | complete makefile (up/down/backup) | ☐ | | |
| C12 | local ci pipeline and github workflow | ☐ | | |
| C13 | NvmShopFloor app with keycloak sso | ☐ | | |
| C14 | adr template and first three records | ☐ | | |
| C15 | oef-mapping and benchmarks skeleton | ☐ | | |
| C16 | finalize readme quickstart | ☐ | | |

**Definition of Done**

| # | Tiêu chí | ☐ | Bằng chứng |
|---|---|---|---|
| ★ D1 | `make up` < 5 phút từ máy sạch | ☐ | `benchmarks.md` |
| D2 | `make test` xanh | ☐ | |
| D3 | Mendix login qua Keycloak, hiện tên + role | ☐ | `docs/evidence/M0-D3-mendix-login.png` |
| D4 | `make ci` xanh + hook chặn được | ☐ | output terminal |
| D5 | Tắt SQL Server → Unhealthy < 10 s, app không crash | ☐ | `benchmarks.md` |

**Sản phẩm phụ bắt buộc**

- [ ] ≥ 3 ADR trong `docs/adr/`
- [ ] `docs/benchmarks.md` có ≥ 4 dòng số thật
- [ ] `docs/oef-mapping.md` có bảng với cột trạng thái
- [ ] README chạy được trong 15 phút, đã tự kiểm chứng

---

## 8. Sau M0

M1 (Factory Model & Manufacturing Service Bus) là nơi bắt đầu chạm vào khái niệm Opcenter thật: bus topology, `ICommand`/`ICommandHandler`, pipeline behavior, Roslyn analyzer. Đọc `scope.md` §5.2 và §9/M1 trước khi lập plan M1.

**Trước khi sang M1**, nếu đã hỏi được đồng nghiệp ([`cau-hoi-cho-dong-nghiep.md`](../cau-hoi-cho-dong-nghiep.md)) thì chỉnh trọng số scope theo bảng §16 và ghi `ADR-000-scope-revision.md`.
