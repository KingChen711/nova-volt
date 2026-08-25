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
| *(mặc định)* | mssql, timescale, rabbitmq, emqx, minio, keycloak | ~5,8 GB *(đo lại sau C08: 4,50 GB cho 4 service đầu)* |
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
  - `GenerateDocumentationFile: true` — **bắt buộc**, nếu không IDE0005 (using thừa) không được báo lúc build
  - ~~`InvariantGlobalization: true`~~ → **BỎ**, xem §5.C02.1
  - `UseArtifactsOutput: true` — gom mọi `bin`/`obj` vào `/artifacts`
- `Directory.Packages.props` — `ManagePackageVersionsCentrally: true` + `CentralPackageTransitivePinningEnabled: true`
- `NovaVolt.Mes.slnx` — SDK .NET 10 mặc định sinh định dạng `.slnx`, xem §5.C02.2
- `Directory.Build.props` riêng trong `tests/` — tắt `TreatWarningsAsErrors` (test hay có warning vô hại). **Phải `Import` thủ công file cha**, vì MSBuild chỉ nạp `Directory.Build.props` gần nhất rồi dừng

**Version package (tra ngày 2026-08-25)**

| Package | Version | Dùng từ |
|---|---|---|
| Microsoft.Extensions.Hosting | 10.0.11 | C03 |
| Serilog.AspNetCore | 10.0.0 | C03 |
| Microsoft.Extensions.TimeProvider.Testing | 10.9.0 | M7 (khai báo trước — nền cho K1) |
| xunit.v3 | 4.0.0 | C04 |
| Microsoft.NET.Test.Sdk | 18.9.0 | C04 |
| Shouldly | 4.3.0 | C04 |

**Kiểm chứng**
```bash
dotnet --version                                   # 10.0.300
dotnet msbuild <project> -getProperty:TargetFramework,TreatWarningsAsErrors,...
dotnet build                                       # Build succeeded
dotnet format NovaVolt.Mes.slnx --verify-no-changes
```
Dựng một project `.probe` tạm để chứng minh props thật sự được nạp và CPM thật sự resolve, rồi xoá đi. **Solution rỗng không chứng minh được gì.**

#### C02.1 — Vì sao BỎ `InvariantGlobalization: true`

Plan ban đầu ghi bật property này. **Sai.** Đo thực tế ngày 2026-08-25 trên Windows:

| | `InvariantGlobalization=true` | `=false` |
|---|---|---|
| `FindSystemTimeZoneById("Asia/Ho_Chi_Minh")` | ❌ `TimeZoneNotFoundException` | ✅ +07:00 |
| `FindSystemTimeZoneById("Europe/Berlin")` | ❌ `TimeZoneNotFoundException` | ✅ hè +02:00 / đông +01:00 |
| `new CultureInfo("de-DE")` | ❌ crash | ✅ |

Trên Windows, ánh xạ ID kiểu IANA → Windows time zone đi qua ICU. Chế độ globalization-invariant không nạp ICU, nên **mọi ID IANA đều fail**. Điều đó phá `IProductionCalendar` ở **M3** — nơi `scope.md` §2.3 yêu cầu đúng hai ID đó và yêu cầu test DST cho site DE1.

Mục tiêu ban đầu của property (chặn format phụ thuộc culture) được thay bằng: `CA1305`/`CA1310` ở mức `warning` trong `.editorconfig`, cộng với set `CultureInfo` mặc định tường minh lúc khởi động ở C03. Ghi **ADR-020** ở C14.

#### C02.2 — `.slnx` thay vì `.sln`

`dotnet new sln` trên SDK 10.0.300 mặc định sinh **`.slnx`** (XML, không có GUID). Đã kiểm: `dotnet build` và `dotnet format --verify-no-changes` đều chạy được với `.slnx` → C12 không bị ảnh hưởng.

Giữ `.slnx`: ít merge conflict hơn, đọc được bằng mắt. Thêm `*.slnx text eol=crlf` vào `.gitattributes`.

**Quyết định đã ghi**: bật `TreatWarningsAsErrors`. Đã kiểm chứng bằng vi phạm cố ý — IDE0161, IDE0011, IDE0005 đều thành `error` và build FAILED.

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
- `src/Platform/Nvm.Kernel/` — nơi ở của `SerialNumber`. Đây là project production đầu tiên; M1 sẽ thêm `ICommand`/`ICommandHandler` vào cùng project này
- `tests/Unit/Nvm.UnitTests/` — xUnit v3 + Shouldly
- **Một test thật**, không phải `Assert.True(true)`: `SerialNumber.Parse` tách site/kind/line/năm/ngày/ca/seq từ chuỗi 16 ký tự (`scope.md` §6.1), kèm đủ case sai định dạng
- `Makefile` với target `help`, `build`, `test`, `format`, `format-check`, `clean`
- Thêm project vào solution

**Kiểm chứng**
```bash
make test                      # 22 passed
# rồi sửa cố ý cho một test đỏ:
make test                      # 21 passed, 1 failed, make thoát code 2
# hoàn nguyên:
make test                      # 22 passed
```

**Ghi chú**: dùng **Shouldly** chứ không FluentAssertions — FluentAssertions từ v8 đổi sang license thương mại. Chi tiết ở `scope.md` §10.1.

#### C04.1 — xunit v3 trên .NET 10: VSTest đã chết

Đây là điểm tốn thời gian nhất của C04, và là kiến thức dùng lại ở mọi milestone sau.

`dotnet test` báo lỗi:
> *Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later.*

Bối cảnh: xunit v3 chạy trên **Microsoft.Testing.Platform (MTP)**, còn `dotnet test` xưa nay là công cụ của **VSTest**. .NET 10 bỏ cầu nối giữa hai thứ đó. Cách làm đúng:

1. Bật MTP mode trong **`global.json`** (không phải `dotnet.config` — đó là ngõ cụt tôi thử trước):
   ```json
   { "test": { "runner": "Microsoft.Testing.Platform" } }
   ```
2. **Bỏ** `Microsoft.NET.Test.Sdk` và `xunit.runner.visualstudio` khỏi test project — cả hai là hạ tầng VSTest. `xunit.v3` đã nhúng sẵn MTP runner.
3. Test project phải là `<OutputType>Exe</OutputType>`.
4. **Cú pháp đổi**: `dotnet test MySolution.slnx` → `dotnet test --solution MySolution.slnx`.

Template `dotnet new xunit` của SDK vẫn sinh ra **xunit v2**, nên project v3 phải dựng tay.

#### C04.2 — `make` trên Windows

`make` không có sẵn. Đã cài `winget install ezwinports.make` (GNU Make 4.4.1). winget **không tạo shim**, nên phải tự thêm vào PATH: thư mục `…\WinGet\Packages\ezwinports.make_*\bin`.

> [!warning] Đừng dùng `setx PATH "%PATH%;..."`
> `%PATH%` lúc chạy đã gồm cả machine PATH, nên lệnh đó copy toàn bộ entry hệ thống vào user PATH — nhân đôi, và sau này có thể che mất bản cập nhật của machine PATH. Dùng `[Environment]::SetEnvironmentVariable('Path', …, 'User')` với giá trị lấy từ đúng scope `User`.

Trong `Makefile` phải ghi `SHELL := sh`. Không ghi thì GNU Make trên Windows đoán shell theo từng dòng — lệnh trông đơn giản thì gọi thẳng `.exe`, lệnh có ký tự đặc biệt thì gọi `sh` — nên cùng một file chạy khác nhau tuỳ dòng. Cụ thể đã gặp: `echo.` (cú pháp cmd) không chạy vì make gọi `echo.exe` của Git; rồi `(CI dung lenh nay)` làm `sh` báo syntax error vì ngoặc đơn không được quote.

#### C04.3 — Hai chỗ `artifacts`

`UseArtifactsOutput` coi **mỗi thư mục có `Directory.Build.props` là một gốc riêng**, nên `tests/` sinh ra `tests/artifacts/` thành chỗ thứ hai. Sửa bằng cách ghi tường minh trong `Directory.Build.props` gốc:
```xml
<ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
```

Cũng phải bỏ `GenerateDocumentationFile=false` khỏi `tests/Directory.Build.props`: chính cờ đó bật IDE0005, tắt nó thì Roslyn cảnh báo ở mọi lần build.

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
- **Ba** container probe (alpine, `sleep infinity`) dưới profile `probe`, không chạy trong `make up` thường:
  - `probe-ot` trên `ot-net`
  - `probe-dmz` trên **`ot-net` + `dmz-net`** — mô phỏng đúng vai trò EMQX ở C08: con đường hợp lệ duy nhất đi từ tầng nhà máy lên
  - `probe-it` trên `it-net`
- `.env.example` — **bảng port** (xem C05.2), `.env` đã gitignore từ C01

**Kiểm chứng**
```bash
docker compose --profile probe up -d
docker network ls --filter name=novavolt            # 3 network
docker network inspect novavolt-mes_ot-net --format '{{.Internal}}'   # true
```
Rồi kiểm ranh giới theo **cả năm chiều**, mỗi chiều nêu rõ kỳ vọng trước khi chạy:

| Chiều | Kỳ vọng | Kết quả thực tế |
|---|---|---|
| IT → OT | bị chặn | `ping: bad address` |
| DMZ → OT | thông | 0% packet loss |
| OT → internet | bị chặn | `sendto: Network unreachable` |
| IT → internet | thông | 0% packet loss |
| DMZ → IT | bị chặn | `ping: bad address` |

> [!warning] Bẫy khi viết script kiểm chứng
> `docker exec ... | tail -3` làm exit code trở thành của `tail`, luôn bằng 0. Dùng `cmd && echo DAT || echo KHONG DAT` sau một pipe như vậy sẽ **luôn báo đạt**. Phải bắt exit code vào biến trước khi lọc output. Một script kiểm chứng báo sai còn tệ hơn không có script.

#### C05.1 — Network mồ côi không được tạo

Compose chỉ tạo network nào có service dùng tới. Khai báo `dmz-net` mà không service nào gắn vào thì nó **không xuất hiện**, và bước kiểm "3 network" sẽ trượt. Đây là lý do cần cả ba probe chứ không chỉ một.

#### C05.2 — Chốt bảng port ngay ở C05

`.env.example` chứa bảng port đầy đủ, tiêu thụ dần từ C06 tới C09. Đây không phải config thừa mà là **bản ghi quyết định**: R-M0-5 đã cảnh báo Keycloak và Mendix Studio Pro cùng mặc định 8080. Chốt sớm để C09 và C13 khỏi sửa chéo — **Mendix giữ 8080, Keycloak nhường sang 8081**.

---

### C06 — `chore(infra): add postgresql with timescaledb`

**Mục tiêu**: store cho telemetry và read model.

**Việc làm**
- Service `timescale`, image **`timescale/timescaledb:2.29.2-pg17`** — xem C06.1 về chọn tag
- Network: `it-net`. Port 5432 map ra ngoài để dùng psql/DBeaver
- Named volume `pgdata`
- `mem_limit: 1g`
- `healthcheck: pg_isready -h 127.0.0.1 ...` — xem C06.2, cờ `-h` là bắt buộc
- Init script `deploy/postgres/init/01-schemas.sql`:
  ```sql
  CREATE EXTENSION IF NOT EXISTS timescaledb;
  CREATE SCHEMA IF NOT EXISTS ts;      -- telemetry
  CREATE SCHEMA IF NOT EXISTS rm;      -- read model
  CREATE SCHEMA IF NOT EXISTS trace;   -- genealogy
  CREATE SCHEMA IF NOT EXISTS ingest;  -- dedup
  ```

**Kiểm chứng**

| Kiểm | Kết quả |
|---|---|
| Extension | `timescaledb v2.29.2`, PostgreSQL 17.11 |
| **License** | `timescale` — **không phải** `apache`. Đây là điều kiện để có compression/CAGG/retention |
| Bốn schema | `ingest`, `rm`, `trace`, `ts` |
| Tính năng TSL chạy thật | `create_hypertable` + `add_compression_policy` + `add_retention_policy` + continuous aggregate — tạo rồi xoá, không lỗi |
| Volume giữ dữ liệu qua `down`/`up` | ghi 42 → down → up → đọc lại 42 |
| Init script **không** chạy lại lần hai | 0 lần xuất hiện trong log |
| Profile `probe` không tự chạy | `docker compose up -d` chỉ khởi động `nvm-timescale` |

**Thời gian khởi động**: cold (kèm pull image) **175 s** · warm **6 s**.

#### C06.1 — Đừng lấy tag `-oss`, và đừng lấy `latest-`

Plan ban đầu gợi ý `timescale/timescaledb-ha:pg17`. Đổi vì hai lý do:

- **`-oss` là bẫy**: bản Apache-2 **thiếu compression, continuous aggregate và retention policy** — đúng ba thứ `scope.md` §8.3 cần. Phải dùng bản Community (TSL), tức tag **không** có hậu tố `-oss`. Kiểm bằng `SHOW timescaledb.license;` → phải ra `timescale`.
- **`latest-pg17` là tag trôi**: build hôm nay và build 6 tháng nữa cho kết quả khác nhau. Pin cứng `2.29.2-pg17`.

Bỏ `-ha` vì nó là image Debian to hơn, dùng `PGDATA` phi tiêu chuẩn (`/home/postgres/pgdata`); bản Alpine thường nhẹ hơn và giữ đúng đường dẫn Postgres chuẩn — quan trọng khi Docker chỉ có 8 GB.

#### C06.2 — `pg_isready` không có `-h` là healthcheck giả

Trong lúc chạy init script, entrypoint của Postgres đặt `listen_addresses=''` và **chỉ mở unix socket**. `pg_isready` mặc định đi qua socket đó, nên nó báo *sẵn sàng* trong khi schema chưa tạo xong. Service phụ thuộc sẽ khởi động quá sớm và fail vì thiếu schema — lỗi khó truy vì nó phụ thuộc thời điểm.

Ép qua TCP bằng `-h 127.0.0.1` thì healthcheck chỉ xanh sau khi init đã xong thật. Sẽ dùng lại đúng nguyên tắc này cho SQL Server ở C07.

> [!note] Init script chỉ chạy một lần
> `/docker-entrypoint-initdb.d` chỉ được thực thi khi volume còn rỗng. Sửa `01-schemas.sql` rồi `up` lại sẽ **không** có tác dụng — phải `docker compose down -v`. Từ M2 mọi thay đổi schema đi qua migration có version, không sửa file init nữa.

---

### C07 — `chore(infra): add sql server`

**Mục tiêu**: store cho event store và write model — chọn có chủ đích để bám sát Opcenter thật.

**Việc làm**
- Service `mssql`, image **`mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04`** — pin cứng theo bài học C06.1. Chọn 2022 chứ không phải 2025 RTM: MES trong nhà máy hiếm khi chạy phiên bản mới nhất
- `ACCEPT_EULA=Y`, `MSSQL_PID=Developer`, **`MSSQL_SA_PASSWORD`** (biến `SA_PASSWORD` cũ đã deprecated)
- Network `it-net`, port 1433, volume `mssqldata`
- `mem_limit: 2g` — SQL Server **từ chối khởi động** nếu thấp hơn
- `healthcheck` chỉ hỏi `SELECT 1`, xem C07.2
- Init script `deploy/mssql/init/01-database.sql`: DB `NovaVolt`, schema `es`, login + user `nvm_app`
- Service phụ `mssql-init` trong **profile `init`**, xem C07.1

**Mật khẩu**: phải đạt độ phức tạp của SQL Server (≥8 ký tự, có hoa/thường/số/ký tự đặc biệt). Kiểu `nvm_dev_only` như Postgres sẽ bị từ chối.

**Kiểm chứng**

| Kiểm | Kết quả |
|---|---|
| Edition / version | Developer Edition, 16.0.4265.3 |
| Database, schema, login, db user | `NovaVolt`, `es`, `nvm_app` (login + user) |
| `nvm_app` đăng nhập, CREATE/INSERT/SELECT/DROP trong `es` | chạy được, không cần `sa` |
| Init chạy lần hai | in `already exists` cho cả 4 đối tượng, **exit 0** |
| Volume giữ dữ liệu qua `down`/`up` | `NovaVolt` vẫn còn |
| Từ volume rỗng: `up --wait` rồi `run --rm mssql-init` | **16 s**, cả hai bước exit 0 |

#### C07.1 — `up --wait` coi container đã thoát là thất bại

Đây là phát hiện quan trọng nhất của C07, và nó ảnh hưởng trực tiếp tới D1.

Đo được:

| Lệnh | Exit code |
|---|---|
| `docker compose up -d --wait timescale mssql` | 0 |
| `docker compose up -d --wait` *(có cả `mssql-init`)* | **1** |
| `docker compose run --rm mssql-init` | 0 |

`--wait` chờ service **running hoặc healthy**. Một container chạy xong rồi thoát — dù thoát **0** — vẫn bị tính là hỏng. Không phải lỗi script.

Cách xử lý: đưa `mssql-init` vào profile `init` để `up --wait` không đụng tới, rồi chạy nó thành bước riêng có mã thoát thật:

```bash
docker compose up -d --wait          # service chạy dài
docker compose run --rm mssql-init   # one-shot, exit code thật
```

`make up` ở **C11 phải gộp đủ hai bước này** — nếu chỉ chạy bước một thì máy sạch sẽ có SQL Server nhưng không có database `NovaVolt`, và D1 trượt theo cách rất khó đoán.

> [!note] Vì sao service .NET về sau không cần `depends_on: service_completed_successfully`
> Vì N15: app không được chết khi database chưa sẵn sàng. Health check phải lazy và app phải retry (xem C10). Nên việc `mssql-init` nằm trong profile không gây ràng buộc gì cho M1 trở đi.

#### C07.2 — Healthcheck cố ý KHÔNG kiểm database

Cùng tinh thần C06.2 nhưng ở dạng khác. Nếu healthcheck của `mssql` đòi `DB_ID('NovaVolt') IS NOT NULL` thì khoá chết: `mssql-init` chờ server healthy mới chạy được, mà server lại chờ init xong mới healthy.

Nên healthcheck chỉ hỏi `SELECT 1`. Việc "database đã sẵn sàng chưa" do bước `mssql-init` trả lời bằng mã thoát.

Cờ `-C` là bắt buộc: `mssql-tools18` mặc định `encrypt=yes`, mà chứng chỉ của server là self-signed. Image này **chỉ có** `mssql-tools18`, không còn bản `mssql-tools` cũ.

#### C07.3 — Init của SQL Server chạy lại mỗi lần, nên phải idempotent

Khác Postgres: không có cơ chế "chỉ chạy khi volume rỗng". Mọi câu lệnh phải bọc `IF NOT EXISTS`. Riêng `CREATE SCHEMA` bắt buộc là câu lệnh đầu batch nên phải bọc qua `EXEC('CREATE SCHEMA es;')`.

`sqlcmd` cần cờ **`-b`** để thoát với mã lỗi khi SQL lỗi — thiếu nó thì container vẫn thoát 0 dù script hỏng, và mọi kiểm tra phía sau trở thành vô nghĩa.

> [!warning] Git Bash nuốt đường dẫn container
> `docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd` chạy trong Git Bash sẽ bị đổi thành `D:/apps/Git/opt/...` rồi báo *no such file*. Đây là MSYS path conversion. Thêm tiền tố `MSYS_NO_PATHCONV=1`, hoặc viết `//opt/...`. Sẽ gặp lại ở C08, C09 và trong recipe của Makefile.

---

### C08 — `chore(infra): add rabbitmq and emqx`

**Mục tiêu**: Manufacturing Service Bus và MQTT broker.

**Việc làm**
- **`rabbitmq:4.3.5-management`** — network `it-net`, port 5672 + 15672, volume, `mem_limit: 512m`. Healthcheck **`check_running` + `check_local_alarms`**, không phải `ping` (xem C08.1)
- **`emqx/emqx:5.10.4`** *(plan cũ ghi 5.8, đã lạc hậu)* — network **`ot-net` + `dmz-net`**, port 1883 + 18083, volume, **`mem_limit: 1g`** (xem C08.3), healthcheck qua **HTTP `/status`** (xem C08.2)
- Đặt user/password cho cả hai. RabbitMQ mặc định `guest/guest` chỉ dùng được từ localhost của chính container — không dùng được cho service khác

**Kiểm chứng**

| Kiểm | Kết quả |
|---|---|
| RabbitMQ management API | RabbitMQ 4.3.5, Erlang 27.3.4.16 |
| RabbitMQ từ chối `guest/guest` | HTTP 401 |
| EMQX `/status` | HTTP 200, `Node emqx@nvm-emqx is started` |
| MQTT publish retained từ `dmz-net` | exit 0 |
| MQTT subscribe từ `dmz-net` | nhận lại đúng payload |
| MQTT từ `it-net` | `Unable to connect (Lookup error)` — **bị chặn** |
| Tổng `mem_limit` sau C08 | 4,50 GB / 8 GB quota |

#### C08.1 — `rabbitmq-diagnostics ping` là healthcheck nông

`ping` chỉ xác nhận Erlang node trả lời, không nói gì về việc broker còn nhận được message. Dùng `check_running && check_local_alarms`: vế sau bắt được trạng thái broker còn sống nhưng **đã chặn publisher** vì hết bộ nhớ hoặc hết đĩa — đúng kiểu hỏng âm thầm sẽ gặp ở M2 khi bơm 5.000 msg/s.

#### C08.2 — `emqx ctl status` báo sai, dùng HTTP thay thế

Healthcheck theo plan làm container `unhealthy` trong khi EMQX chạy hoàn toàn bình thường:

```
healthcheck : Node 'emqx@nvm-emqx' not responding to pings.   (exit 1)
log         : EMQX Enterprise 5.10.4 is running now!
/status     : HTTP 200, "Node emqx@nvm-emqx is started"
```

`emqx ctl` đi qua Erlang distribution và không nối được trong image này. Đổi sang `curl -fsS http://localhost:18083/status`.

Đây là lần thứ ba trong M0 cùng một nguyên tắc: **healthcheck phải thử đúng đường mà consumer thật đi**, không phải đường ống nội bộ. C06.2 là TCP thay vì unix socket; C07.2 là `SELECT 1` thay vì kiểm database; ở đây là HTTP thay vì Erlang distribution.

#### C08.3 — EMQX cần 1 GB, không phải 512 MB

Đo được **411 MiB ngay lúc rảnh** với `mem_limit: 512m`, tức 80%. EMQX bật cảnh báo `high_system_memory_usage` từ ngưỡng 70%, và broker trong trạng thái cảnh báo sẽ **chặn publisher** — đúng thứ giết mục tiêu 5.000 msg/s ở M2. Nâng lên 1 GB thì còn 38%.

> [!warning] Lại dương tính giả trong script kiểm chứng
> Phép thử "it-net không tới được EMQX" ban đầu dùng `mosquitto_pub -W 5`. Nhưng `-W` chỉ có ở `mosquitto_sub`, nên lệnh fail vì **sai cú pháp** và script báo "đạt". Cùng loại lỗi đã cảnh báo ở C05: phải xác nhận nó thất bại **đúng lý do**, không chỉ xác nhận nó thất bại. Kết quả đúng phải là `Unable to connect (Lookup error)`.

**Ghi chú kiến trúc**: EMQX cố ý **không** nằm trên `it-net`. Service .NET muốn nghe MQTT phải qua `Nvm.EdgeGateway` ở `dmz-net` (M2). Port 1883 vẫn publish ra host được, vì EMQX có một chân trên `dmz-net` không phải mạng internal.

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
- **ADR-020** — *Không bật InvariantGlobalization*: theo §5.C02.1. Ghi kèm bảng đo thực tế — đây là bằng chứng, không phải phỏng đoán

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
