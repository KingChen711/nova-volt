---
title: "M0 — Bootstrap & Walking Skeleton"
milestone: M0
duration: "1 tuần (~12 giờ)"
status: done
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
| ★ D1 | `make up` sau `make down-v` (image đã cache) → tất cả health check xanh trong **< 5 phút** | `make up` in ra thời gian → ghi vào `docs/benchmarks.md` |
| D2 | `make test` chạy được và xanh | Output test runner |
| D3 | Mendix app đăng nhập bằng Keycloak, hiện tên + role của user | Hai lệnh `curl` tái lập được (xem C13) + dòng ghi ngày trong `mendix/README.md` |
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
| Q2 | Mendix Studio Pro | **11.12.3** | OIDC SSO yêu cầu Mendix 9.0+ → tương thích. Cần kèm **Community Commons**. *(Plan gốc ghi 11.12.1; bản thật sự dùng để tạo app là **11.12.3** — sửa ở R5, xem `mendix/README.md`)* |
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
| C14 | `docs: add adr template and first four records` | E — Đóng M0 | 60' | C02, C06, C07 |
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

> [!warning] Bảng năm chiều này **thiếu ba chiều**, và chỗ thiếu đã thành lỗ hổng thật
> Cả năm phép trên đều đi bằng **tên container**, nên chúng chỉ chứng minh đường *trực tiếp*
> bị chặn. Không phép nào hỏi *"đi vòng qua port host thì sao?"* — và câu trả lời suốt từ M0
> tới M1 là **đi được**. Xem §C08.4. Bảng đúng có 9 phép, chạy bằng `make net-check`.

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

**Ghi chú kiến trúc**: EMQX cố ý **không** nằm trên `it-net`. Service .NET muốn nghe MQTT phải qua `Nvm.EdgeGateway` ở `dmz-net` (M2).

> [!caution] Câu cuối của mục này từng SAI, và đã được sửa
> Nguyên văn cũ: *"Port 1883 vẫn publish ra host được, vì EMQX có một chân trên `dmz-net`
> không phải mạng internal."* Vế **vì sao** thì đúng — đúng là nhờ chân `dmz-net` mà Docker
> mới publish được. Vế **kết luận** thì sai: nó coi việc publish được là an toàn, trong khi
> đó chính là lỗ hổng K11. Xem §C08.4.

#### C08.4 — Port publish ra host là một lỗ K11, và `127.0.0.1` không vá được

Phát hiện khi audit M0/M1 (2026-08-27). `emqx` đã bỏ `ports:` từ đây.

**Số đo 1 — lỗ hổng có thật.** Từ `nvm-timescale` (chỉ có chân `it-net`):

| Đích | Kết quả |
|---|---|
| `nvm-emqx:1883` | **closed** — ranh giới trực tiếp hoạt động đúng |
| `host.docker.internal:1883` | **OPEN** |
| `host.docker.internal:18083` | **OPEN** |

Ranh giới network chỉ chặn đường trực tiếp. Port host là một cửa hông mà mọi container trên
mọi network đều đẩy vào được.

**Số đo 2 — bind loopback KHÔNG phải ranh giới.** Hai container tạm trên `dmz-net`, một
publish `127.0.0.1:19998`, một publish `0.0.0.0:19999`. Đo lại từ `nvm-timescale`:

| Đích | Kết quả |
|---|---|
| `host.docker.internal:19998` *(bind loopback)* | **OPEN** |
| `host.docker.internal:19999` *(bind 0.0.0.0)* | **OPEN** |

Proxy của Docker Desktop nối tới host **qua chính loopback**, nên `127.0.0.1` không phân biệt
được container với người dùng. Phương án "bind loopback cho an toàn" bị loại **bằng số đo**,
không phải bằng lập luận.

**Số đo 3 — chỉ `ot-net` thì không publish được gì.** Container chỉ nằm trên network
`internal: true` bị Docker **bỏ qua hoàn toàn** khai báo `ports:` — mapping không xuất hiện.
EMQX publish được **chỉ vì** nó có chân `dmz-net`. Đây đúng là điều C08.3 nhận xét, nhưng
kết luận ngược dấu.

**Cách sửa**: bỏ `ports:` của `emqx`. Đường hợp lệ cho người dùng là `make dmz-shell` —
một container trên `dmz-net` (và **chỉ** `dmz-net`, không có chân `ot-net`, vì máy trạm của
con người không được đứng hai chân sang tầng thiết bị). Đây là jump host, thu nhỏ.

**Chống tái phát**: `make net-check` → `scripts/net-check.sh`, 9 phép đo bằng TCP connect
thật, exit ≠ 0 nếu thủng. Đã kiểm bằng cách phá hoại: publish lại 1883/18083 thì **đúng 2/9**
phép trượt, 7 phép còn lại vẫn đạt — script chỉ đúng chỗ, không đỏ vơ đũa cả nắm.

> [!warning] Lần thứ ba dính đúng một loại bẫy trong cùng một chủ đề
> Bản đầu của `net-check.sh` gọi `docker exec probe-it`, nhưng `container_name` là
> `nvm-probe-it`. Docker trả về *"No such container"* và mã thoát khác 0 — **y hệt một kết
> nối bị chặn**. Kết quả: cả 9 chiều báo `blocked`, bảng trông như một ranh giới cực kỳ chắc
> chắn, trong khi thực tế không đo được gì cả.
>
> Nó bị lộ vì phép MQTT ngay bên dưới dùng tên container đúng và **đạt** — hai dòng nói ngược
> nhau về cùng một đường. Nếu bảng chỉ toàn TCP thì lỗi này đã lọt.
>
> Vì thế script có `assert_alive()`: probe không chạy nổi `true` thì thoát mã 2, **không**
> báo `blocked`. Sai theo hướng yên tâm là kiểu sai nguy hiểm nhất ở đây.

---

### C09 — `chore(infra): add minio and keycloak realm`

**Mục tiêu**: object storage cho evidence, và identity provider cho cả .NET lẫn Mendix.

**Việc làm**
- **`minio/minio:RELEASE.2025-09-07T16-13-09Z`** — `it-net`, port 9000 + 9001, volume, `mem_limit: 512m`. Healthcheck `mc ready local` (image có sẵn `mc`)
- `minio-init` dùng `minio/mc` tạo bucket `evidence`, `vision` — **profile `init`**, cùng lý do C07.1
- **`quay.io/keycloak/keycloak:26.7.2`** — `start-dev --import-realm`, `it-net`, port **8081**, **`mem_limit: 1g`** (xem C09.4)
- `deploy/keycloak/realm-novavolt.json` + `deploy/keycloak/README.md` (chú thích để ở README, xem C09.1)
- **Không có volume cho H2** — xem C09.3

**Kiểm chứng**

| Kiểm | Kết quả |
|---|---|
| Import realm | `Realm 'novavolt' imported` · `Import finished successfully` |
| Token `op.nv1` | `site_id=NV1`, `roles=['Operator']` |
| Token `qa.nv1` | `site_id=NV1`, `roles=['QaEngineer']` |
| Token `op.de1` | `site_id=DE1`, `roles=['Operator']` |
| MinIO bucket | `evidence/`, `vision/` — chạy lại lần hai exit 0 |
| Endpoint | MinIO API 200 · console 200 · Keycloak discovery 200 |
| **Toàn bộ M0 từ `down`** | **58 s** (6 service 53 s + init 5 s) — ngưỡng D1 là 300 s |
| Tổng `mem_limit` | **6,00 GB / 8 GB quota** |

#### C09.1 — Không được chú thích trong realm JSON

Keycloak deserialize bằng Jackson **strict**: một trường lạ làm import **chết hẳn**, không
phải bỏ qua. Thêm `"_comment": "…"` cho dễ đọc cho ra:

```
ERROR: Failed to run import
ERROR: Unrecognized field "_comment_roles" … not marked as ignorable
```

Triệu chứng bên ngoài là container restart liên tục và healthcheck unhealthy — **không hề
gợi ý** rằng nguyên nhân là một dòng chú thích. Chú thích chuyển hết sang
`deploy/keycloak/README.md`.

#### C09.2 — `site_id` bị nuốt âm thầm nếu không khai báo User Profile

Từ bản 24, Keycloak bật **User Profile** mặc định và **loại bỏ mọi attribute không được
khai báo**. Chỉ viết `"attributes": {"site_id": ["NV1"]}` trên user thì:

- import **thành công**, không cảnh báo;
- user hiện bình thường trong console;
- token **không bao giờ** có claim `site_id`.

Phải thêm khối `components → org.keycloak.userprofile.UserProfileProvider` khai báo
`site_id` và đặt `unmanagedAttributePolicy: ENABLED`. Giá trị của `kc.user.profile.config`
là **chuỗi JSON lồng trong JSON** — kiểm cú pháp lớp trong bằng script trước khi khởi động.

> [!important] Đây là lý do phải giải mã token chứ không tin log
> `Import finished successfully` vẫn in ra khi `site_id` đã bị nuốt. Chỉ có việc lấy token
> thật và đọc payload mới phát hiện được.

#### C09.3 — Không gắn volume cho H2, và đó là quyết định thiết kế

Lý do kỹ thuật: named volume gắn vào `/opt/keycloak/data/h2` được Docker tạo với chủ sở hữu
`root`, còn Keycloak chạy bằng uid 1000 → H2 chết với `AccessDeniedException` ngay lúc khởi
động.

Nhưng lý do đáng giữ là **realm-as-code**: không có volume thì mỗi lần khởi động là một lần
import lại từ file, `realm-novavolt.json` trở thành nguồn sự thật duy nhất, và mọi thay đổi
bấm tay trong Admin Console biến mất khi restart — buộc thay đổi phải đi qua file và được commit.

#### C09.4 — Ngân sách RAM đã chạm mức cần chú ý

| Service | `mem_limit` | Dùng thật lúc rảnh |
|---|---|---|
| mssql | 2,0 GB | 883 MiB |
| timescale | 1,0 GB | 25 MiB |
| emqx | 1,0 GB | 349 MiB |
| **keycloak** | **1,0 GB** | 625 MiB |
| rabbitmq | 0,5 GB | 126 MiB |
| minio | 0,5 GB | 79 MiB |
| **Tổng** | **6,00 GB** | ~2,1 GB |

Keycloak ban đầu để 768m, đo được 597 MiB (78%) khi **chưa ai đăng nhập** — JVM sát trần thì
đợt đăng nhập hàng loạt đầu tiên từ Mendix ở M4 sẽ bị OOM-kill. Nâng lên 1 GB.

> [!warning] Ảnh hưởng tới M13
> Profile mặc định đã chiếm 6,00 GB / 8 GB. Bật thêm profile `obs` (~1,5 GB) sẽ lên ~7,5 GB —
> quá sát. Tới M13 phải chọn: nâng quota Docker lên 10–12 GB, hoặc tắt bớt service khi debug
> observability. Ghi lại để không bất ngờ.

**Ghi chú**: Keycloak 26 dùng `KC_BOOTSTRAP_ADMIN_USERNAME`/`PASSWORD`, biến `KEYCLOAK_ADMIN`
cũ đã deprecated. Health endpoint nằm ở **management port 9000 trong container**, không phải
8080, và image **không có `curl` lẫn `wget`** — healthcheck phải mở socket bằng bash `/dev/tcp`.

---

### C10 — `feat(host): add readiness checks for all dependencies`

**Mục tiêu**: `/health/ready` phản ánh **đúng** tình trạng thật của toàn hệ thống.

**Việc làm**
- Package: `AspNetCore.HealthChecks.SqlServer`, `.NpgSql`, `.Uris` (dùng chung cho Keycloak, MinIO **và** RabbitMQ) — **ba** package, không phải bốn
- **Năm** check, không phải sáu — xem C10.1 về EMQX
- Mỗi check có tên riêng, tag `ready`, `timeout: 3s`
- `/health/ready` và `/health/live` trả JSON chi tiết: tên, status, duration, error (**chỉ ở Development**)
- Cấu hình đọc từ **`.env`** qua `DotEnvLoader` — xem C10.2

**Kiểm chứng — đây là D5**

| Kiểm | Kết quả |
|---|---|
| 5 probe lúc bình thường | tất cả `Healthy`, tổng 451 ms |
| Tắt SQL Server → phát hiện | **3,2 s** *(ngưỡng: < 10 s)* |
| 4 check còn lại | vẫn `Healthy` — không lan |
| `/health/live` | vẫn `Healthy` |
| Process | sống, `GET /` → 200 |
| Bật lại SQL Server → phục hồi | **13 s**, `Application started` chỉ xuất hiện 1 lần → **không restart** |
| **Khởi động app KHI SQL Server đang tắt** | app lên sau **2 s**, `live=Healthy`, `ready=Unhealthy[sqlserver]` |

Dòng cuối là bằng chứng mạnh nhất cho **N15**: app không chết vì dependency chết.

#### C10.1 — Bỏ EMQX khỏi readiness, không phải quên

Plan ghi thêm check TCP tới EMQX port 1883. **Không làm**, vì mâu thuẫn với ranh giới đã dựng
ở C05/C08: EMQX nằm trên `ot-net` + `dmz-net`, không có gì ở tầng IT nói chuyện với nó.
Service duy nhất cần nó là `Nvm.EdgeGateway`, và nó chỉ xuất hiện ở **M2**.

Readiness cho một dependency mà process không dùng nghĩa là: EMQX chết thì app bị rút khỏi
load balancer **vô cớ**. Nguyên tắc: *readiness chỉ gồm dependency mà thiếu nó thì app không
phục vụ được request.* Thêm EMQX ở M2, cùng lúc EdgeGateway ra đời.

#### C10.2 — Một nguồn sự thật cho cấu hình

Cách chuẩn .NET là `appsettings.Development.json`. Ở đây nó tạo ra **hai file cùng chứa sáu
mật khẩu** phải giữ đồng bộ bằng tay — và chúng sẽ trôi.

Thay bằng `DotEnvLoader`: ~30 dòng, **chỉ chạy ở Development**, đọc `.env` ở thư mục cha gần
nhất, và **biến môi trường đã có luôn thắng** nên `NVM_X=... dotnet run` vẫn override được.
`.env` là thứ docker-compose đã đọc, nên cả hai bên dùng chung một nguồn.

#### C10.3 — RabbitMQ kiểm qua endpoint alarms, không phải cổng AMQP

Dùng `/api/health/checks/alarms` của management API, cùng lý do C08.1: broker đã chặn
publisher vì alarm bộ nhớ/đĩa **vẫn** trả `200` ở `/api/overview` và **vẫn** mở cổng 5672.

Cách này cũng tránh được cái bẫy nguy hiểm hơn: thư viện health check của RabbitMQ muốn có
sẵn một `IConnection` trong DI, mà tạo connection lúc đăng ký service nghĩa là **app không
khởi động nổi khi broker đang tắt** — đúng thứ N15 cấm.

#### C10.4 — `dotnet build` KHÔNG ép quy tắc naming, chỉ `dotnet format` mới ép

Phát hiện khi `make format-check` đỏ với `IDE1006` trong khi `make build` xanh. Kiểm lại bằng
một file vi phạm cố ý:

| Lệnh | Kết quả |
|---|---|
| `dotnet build` | **Build succeeded** — bỏ lọt |
| `dotnet format --verify-no-changes` | `error IDE1006: Naming rule violation` |

Nghĩa là `EnforceCodeStyleInBuild` (bật ở C02) phủ các rule `IDExxxx` về style **nhưng không
phủ naming rule**. Người gác cổng thật cho naming là `make ci`/`make format-check`, không phải
build. Đáng nhớ vì nó làm hỏng giả định "build xanh là code đúng quy ước".

Nguyên nhân gốc của lỗi: quy tắc `_camelCase` ở C01 áp cho **mọi** private field, kể cả
`private static readonly` — thứ mà C# quy ước viết PascalCase. Đã thêm rule riêng, **đặt
trước** rule chung vì rule khớp đầu tiên thắng.

---

### C11 — `chore: complete makefile with up, down, backup`

**Mục tiêu**: một lệnh duy nhất, và một con số cho D1.

**Việc làm**
- Target: `help`, `up`, `up-obs`, `down`, `down-v`, `reset`, `ps`, `logs`, `build`, `test`, `format`, `format-check`, `clean`, `backup`
- **`up` phải gộp HAI bước** — xem C11.1. Đây là điểm dễ sai nhất
- `backup` — git bundle sang OneDrive, **kèm `git bundle verify`** (xem C11.3). `BACKUP_DIR` đọc từ `.env`
- Guard `.env` — thiếu file thì báo rõ, không để `docker compose` kêu về biến không resolve được
- Target `ci` **không** thuộc C11, nó là của C12 cùng hook và `ci.yml`

**Kiểm chứng — đây là D1** ★

| Lần | Điều kiện | Thời gian | `make` exit |
|---|---|---|---|
| 1 | sau `make down-v` (volume rỗng, image đã cache) | **48 s** | 0 |
| 2 | sau `make down` (giữ volume) | **36 s** | 0 |
| 3 | sau `make down` (giữ volume) | **42 s** | 0 |

Ngưỡng D1 là 300 s → **đạt với biên rất rộng**.

Bằng chứng bước init thật sự chạy sau `down-v`: database `NovaVolt` tồn tại và PostgreSQL có
đủ 4 schema. Nếu thiếu bước hai thì hai kiểm tra này sẽ trượt trong khi `make up` vẫn in
"All healthy".

Guard `.env`: đổi tên `.env` đi rồi chạy `make up` → in hướng dẫn `cp .env.example .env` và
thoát mã 2.

#### C11.1 — `make up` là hai lệnh, không phải một

Đây là chỗ C07.1 đã cảnh báo và rất dễ quên:

```make
up: .env
	@start=$$(date +%s); \
	$(COMPOSE) up -d --wait || exit 1; \
	$(COMPOSE) run --rm mssql-init || exit 1; \
	$(COMPOSE) run --rm minio-init || exit 1; \
	echo "All healthy in $$(($$(date +%s)-start))s"
```

Chỉ chạy bước một thì máy sạch sẽ có SQL Server **nhưng không có database `NovaVolt`**, và
MinIO không có bucket — trong khi `make up` vẫn in "All healthy". Hỏng theo kiểu tệ nhất: im
lặng, và chỉ lộ ra ở milestone sau.

`|| exit 1` sau mỗi bước là bắt buộc: các dòng nối bằng `\` chạy trong **cùng một shell**, nên
lệnh giữa chừng fail sẽ không tự dừng.

#### C11.2 — Đọc riêng một biến, đừng `include .env`

`include .env` nạp **mọi** biến vào make, kể cả mật khẩu. Tệ hơn: một khoá trùng tên với biến
đặc biệt của make (`SHELL`, `MAKEFLAGS`…) sẽ phá cả file mà không báo lý do. Dùng:

```make
BACKUP_DIR := $(shell grep -E '^NVM_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2-)
```

`down` cũng phải nêu **đủ profile** (`--profile probe --profile init --profile obs`), nếu không
container của profile không active sẽ bị bỏ lại.

#### C11.3 — Backup chưa verify thì không phải backup

`make backup` chạy `git bundle create` **rồi `git bundle verify`**. Một file bundle hỏng trông
y hệt một file bundle tốt cho tới ngày cần khôi phục. Kiểm chứng: bundle 153 KB, verify OK,
`git bundle list-heads` trả về đúng HEAD hiện tại.

**Nếu quá 5 phút**: nghi phạm số một là SQL Server. Xử lý theo thứ tự — tăng `mem_limit` lên
2,5 g; giảm `healthcheck.interval`; nới `start_period`. Vẫn không đạt thì **đề xuất sửa DoD**,
không phải bỏ bớt service. Theo `AGENTS.md` §3.2.

---

### C12 — `ci: add local ci pipeline and github workflow`

**Mục tiêu**: có người gác cổng chạy được **ngay hôm nay**, và sẵn sàng chuyển lên GitHub khi cần.

**Việc làm**
- Target **`make ci`** — `restore → format → build -c Release → test -c Release --no-build`
- Target **`make hooks`** — đặt `core.hooksPath`, xem C12.2
- `.githooks/pre-commit` — **chỉ** kiểm format, và **bỏ qua hẳn** khi commit không đụng `.cs` (xem C12.3)
- `.github/workflows/ci.yml` viết sẵn, các bước khớp `make ci`, `permissions: contents: read`, cache NuGet, đọc SDK từ `global.json`
- Chưa thêm Testcontainers — integration test đến ở M5, khi đó tách job riêng

**Kiểm chứng — đây là D4**

| # | Bước | Kết quả |
|---|---|---|
| 1 | `make ci` | xanh, exit **0** |
| 2 | Phá thụt lề 1 file → `make ci` | **ĐỎ ở bước format**, exit 2, hai lỗi `WHITESPACE` |
| 3 | `git commit` | **hook CHẶN**, exit 1, in hướng dẫn `make format` |
| 4 | `make format` | file trở lại **giống hệt bản gốc** (`diff -q` sạch) |
| 5 | `make ci` | xanh trở lại, exit 0 |
| 6 | Commit chỉ có `.md` | hook **bỏ qua**, commit đi thẳng |

Bước 2 xác nhận thứ tự đúng: đỏ ở **format**, chưa tới build — một lỗi thụt lề không phải chờ
hết một lần build Release mới lộ ra.

#### C12.1 — `make ci` và `ci.yml` phải khớp từng cờ

Hai bên lệch nhau thì CI không còn là thứ dự đoán được, và người ta học cách bỏ qua nó. Cụ thể
những chỗ dễ trôi: `-c Release`, `--no-restore`, `--no-build`, và **`--solution`** (bắt buộc ở
MTP mode, xem C04.1).

`ci.yml` **không** ghi cứng version .NET — nó đọc `global.json` qua `global-json-file`. Khai báo
version ở hai chỗ là tạo sẵn cơ hội cho chúng lệch nhau.

#### C12.2 — Hook không tự cài khi clone

Git **cố ý** bỏ qua hook đi kèm repo vì lý do bảo mật — một repo lạ không được phép chạy script
trên máy bạn lúc clone. `core.hooksPath` là đường chính thức để trỏ sang thư mục được version hoá,
nhưng nó là **git config local**, phải chạy một lần trên mỗi máy:

```bash
make hooks
```

Ghi vào README ở C16, nếu không thì dev thứ hai sẽ tưởng hook đang chạy trong khi nó không.

#### C12.3 — Hook chỉ kiểm format, và bỏ qua khi không có `.cs`

Hai quyết định, cùng một lý do: **hook chậm là hook bị vô hiệu hoá**.

- **Không build, không test trong hook.** Build Release + test mất ~10 s. Sau vài lần chờ, người
  ta sẽ gõ `--no-verify` cho nhanh — và mất luôn cả phần kiểm format vốn rất rẻ. Build/test là
  việc của `make ci`.
- **Bỏ qua hẳn khi commit không đụng `.cs`.** Sửa tài liệu không có lý do gì phải chờ
  `dotnet format`. Đã kiểm: commit một file `.md` đi thẳng, không dừng.

Hook cũng in sẵn lối thoát `git commit --no-verify` — giấu nó đi không làm ai an toàn hơn, chỉ
làm người bị chặn mất thêm thời gian tra cứu.

#### C12.4 — Hook thiếu bit executable bị bỏ qua IM LẶNG

Git ghi file mới vào index với mode `100644`. Trên Windows không sao — Git for Windows chạy hook
qua `sh` bất kể mode. Nhưng trên **Linux/macOS, một hook không có bit executable bị bỏ qua mà
không báo gì**: không lỗi, không cảnh báo, chỉ là hook không bao giờ chạy.

```bash
git update-index --chmod=+x .githooks/pre-commit    # 100644 -> 100755
```

Kiểm bằng `git ls-files -s .githooks/pre-commit` — phải thấy `100755`. Đây là loại lỗi chỉ lộ ra
khi có người thứ hai clone repo trên máy khác, và lúc đó rất khó đoán vì "trên máy tôi vẫn chạy".

**Khi có remote sau này**: không phải viết lại gì — `git remote add` rồi push là `ci.yml` chạy.

---

### C13 — `feat(mendix): add NvmShopFloor app with keycloak sso`

**Mục tiêu**: chứng minh đường đi từ Mendix tới identity — nền cho mọi màn hình sau này.

> [!important] Mendix có repo riêng
> App Mendix sống trong **Team Server repo của chính nó**, không nằm trong repo `novavolt-mes`. Trong repo chính, commit này chỉ thêm `mendix/README.md`. Công việc mô hình hoá được commit riêng trong Team Server — đã xong: `cc8c9db` *(`feat(mendix): add NvmShopFloor app with keycloak sso`)*.

**Việc làm — phía Mendix Studio Pro 11.12.3**
- Tạo app `NvmShopFloor` từ Blank template
- Cài module **OIDC SSO** từ Marketplace *(yêu cầu Mendix 9.0+, nên 11.12.3 dùng được)* kèm dependency bắt buộc **Community Commons**. Bản OIDC ≤ 4.3.0 cần thêm **Encryption** — kiểm tra version lúc cài, đừng cài thừa
- Cấu hình OIDC trỏ tới Keycloak realm `novavolt`, client `nvm-mendix`
- **Chạy bằng `Run Locally`** — không cần Mendix Cloud node ở milestone này (xem §3, Q3)
- Tạo module `NvmShared` (chuẩn bị cho C-về-sau: connector POM, response mapper)
- Một trang `Home_Web`: hiện `Tên user`, `Email`, danh sách role, `site_id`
- Module role `Operator`, `LineLeader` map từ realm role
- `mx check` sạch

**Việc làm — phía repo chính**
- `mendix/README.md`: tên app, URL Team Server, phiên bản Studio Pro, các module Marketplace đã dùng + version, cách chạy local, ghi chú "không commit `.mpr` dirty"

**Kiểm chứng — đây là D3**

Hai lệnh chạy được không cần trình duyệt, không cần phiên đăng nhập — đây là phần **tái lập
được** của bằng chứng:

```bash
# 1. App đẩy đúng authorization request sang Keycloak.
#    Ra 302 nghĩa là cấu hình IdP đang Active VÀ Default.
curl -s -i http://localhost:8080/oauth/v2/login | grep -i "^location"
#    Location phải chứa: client_id=nvm-mendix · response_mode=form_post
#    redirect_uri=http://localhost:8080/oauth/v2/callback · code_challenge_method=S256

# 2. Keycloak phát đúng claim. Giải mã payload, đừng tin log.
curl -s -d "client_id=nvm-mendix" -d "client_secret=dev-only-not-a-secret"      -d "username=op.nv1" -d "password=dev" -d "grant_type=password"      -d "scope=openid profile email"      http://localhost:8081/realms/novavolt/protocol/openid-connect/token
#    Payload phải có: mendix_roles=["Operator"] · site_id="NV1"
```

Phần còn lại cần người thật vì có ô mật khẩu: `Run Locally` → mở
`http://localhost:8080/oauth/v2/login` → đăng nhập `op.nv1` → trang chủ hiện **FullName ·
Email · Site · danh sách role**. `site_id` đúng chứng minh attribute mapping chạy; role
`Operator` đứng cạnh role mặc định `User` chứng minh microflow ATP chạy.

Ghi kết quả quan sát đó thành **một dòng có ngày** trong `mendix/README.md` §Trạng thái D3.
**Không dùng ảnh chụp màn hình** — xem ghi chú bên dưới.

> [!important] Bỏ bằng chứng dạng ảnh — quyết định 2026-08-26
> Bản đầu của plan yêu cầu `docs/evidence/M0-D3-mendix-login.png`. Bỏ hẳn, và bỏ luôn thư mục
> `docs/evidence/`.
>
> Lý do: `AGENTS.md` §3.2 đã định nghĩa bằng chứng là *"số đo, output test, hoặc đoạn code cụ
> thể"* — không có ảnh. Plan tự thêm ảnh vào là **đi lệch khỏi AGENTS.md**, mà AGENTS.md đứng
> trên mọi plan. Ảnh chụp còn thua ở ba điểm thực dụng: không diff được, không chạy lại được
> trong `make ci`, và hỏng lặng lẽ khi UI đổi.
>
> Thay bằng: lệnh tái lập được cho phần máy kiểm được, cộng một dòng có ngày cho phần chỉ mắt
> người thấy. Áp dụng cho **mọi milestone sau**, không riêng M0.
>
> **Không liên quan tới bucket `evidence` của MinIO ở C09.** Cái đó là evidence *nghiệp vụ*
> (raw curve, ảnh vision, WORM) — nằm trong scope sản phẩm, giữ nguyên.

**Bẫy đã biết**: Keycloak chạy trong Docker ở `localhost:8080`, còn Mendix chạy ở `localhost:8080` mặc định → **đụng port**. Đổi Mendix sang `8090`, hoặc đổi Keycloak sang `8081`. Quyết định sớm, vì đổi sau phải sửa cả realm config.

---

### C14 — `docs: add adr template and first four records`

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

**Kiểm chứng**

Mọi lệnh trong quickstart phải đã chạy được ít nhất một lần trên máy này. Đã chạy:
`make up` (hạ tầng healthy), `make build`, `make test` (22 pass), `dotnet run` +
`curl /health/ready` (Healthy, 5 check).

> [!note] Bỏ bài kiểm "máy sạch, bấm giờ 15 phút" — 2026-08-26
> Bản đầu yêu cầu `docker system prune -f` rồi làm lại từ đầu và bấm giờ. Bỏ vì chi phí không
> tương xứng: nó xoá image của **cả máy**, mất chục phút, và thứ nó phát hiện thêm so với
> "chạy thử từng lệnh" chỉ là thời gian tải image — một con số phụ thuộc đường truyền chứ
> không phụ thuộc repo.
>
> ★D1 cũng đổi theo, từ *"máy sạch"* thành *"sau `down-v`, image đã cache"* — phát biểu đúng
> thứ đã đo (48/36/42 s so với ngưỡng 300 s) thay vì một điều kiện chưa bao giờ chạy. Đo lại
> trên máy thật sạch khi nào có người thứ hai clone repo.

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
| C01 | initialize repository skeleton | ☑ | 2026-08-25 | `2fed197` |
| C02 | central build and package configuration | ☑ | 2026-08-25 | `1654ed7` |
| C03 | minimal host with liveness endpoint | ☑ | 2026-08-25 | `e35db85` |
| C04 | unit test project and make test target | ☑ | 2026-08-25 | `1b29599` |
| C05 | docker networks and compose skeleton | ☑ | 2026-08-25 | `134301a` |
| C06 | postgresql with timescaledb | ☑ | 2026-08-25 | `b25d9a7` |
| C07 | sql server | ☑ | 2026-08-25 | `4433939` |
| C08 | rabbitmq and emqx | ☑ | 2026-08-25 | `e0d3c81` |
| C09 | minio and keycloak realm | ☑ | 2026-08-25 | `3aebc86` |
| C10 | readiness checks for all dependencies | ☑ | 2026-08-26 | `4f6408f` |
| C11 | complete makefile (up/down/backup) | ☑ | 2026-08-26 | `0719cad` |
| C12 | local ci pipeline and github workflow | ☑ | 2026-08-26 | `192cdf2` |
| C13 | NvmShopFloor app with keycloak sso | ☑ | 2026-08-26 | `8d4ccfa`. D3 đã chạy đầu-cuối |
| C14 | adr template and first four records | ☑ | 2026-08-26 | `9025a32` |
| C15 | oef-mapping and benchmarks skeleton | ☑ | 2026-08-26 | `f6f8fb8` |
| C16 | finalize readme quickstart | ☑ | 2026-08-26 | commit cuối của M0 |

**Definition of Done**

| # | Tiêu chí | ☐ | Bằng chứng |
|---|---|---|---|
| ★ D1 | `make up` < 5 phút sau `down-v`, image đã cache | ☑ 2026-08-26 | **48 / 36 / 42 s** — `benchmarks.md` |
| D2 | `make test` xanh | ☑ 2026-08-26 | 22 test, 0 fail, ~10 s |
| D3 | Mendix login qua Keycloak, hiện tên + role | ☑ 2026-08-26 | 2 lệnh `curl` ở C13 + `mendix/README.md` §Trạng thái D3 |
| D4 | `make ci` xanh + hook chặn được | ☑ 2026-08-26 | 6 bước kiểm ở §C12, exit 0 / 2 / hook chặn |
| D5 | Tắt SQL Server → Unhealthy < 10 s, app không crash | ☑ 2026-08-26 | **3,2 s**, app sống, phục hồi 13 s — `benchmarks.md` |

**Sản phẩm phụ bắt buộc**

- [x] ≥ 3 ADR trong `docs/adr/` — có **4**: 001, 002, 019, 020
- [x] `docs/benchmarks.md` có ≥ 4 dòng số thật — có **12**
- [x] `docs/oef-mapping.md` có bảng với cột trạng thái — 22 dòng
- [x] README quickstart viết xong, mọi lệnh trong đó đã chạy ít nhất một lần

---

## 8. Sau M0

M1 (Factory Model & Manufacturing Service Bus) là nơi bắt đầu chạm vào khái niệm Opcenter thật: bus topology, `ICommand`/`ICommandHandler`, pipeline behavior, Roslyn analyzer. Đọc `scope.md` §5.2 và §9/M1 trước khi lập plan M1.

**Trước khi sang M1**, nếu đã hỏi được đồng nghiệp ([`cau-hoi-cho-dong-nghiep.md`](../cau-hoi-cho-dong-nghiep.md)) thì chỉnh trọng số scope theo bảng §16 và ghi `ADR-000-scope-revision.md`.
