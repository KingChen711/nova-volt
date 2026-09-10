# Equipment PoC — M4/C02

Backend đã có `GET /pom/v1/Equipment`, lookup theo key, `$count` và `$metadata` có JWT.
PoC trên Mendix **đã nghiệm thu ngày 2026-09-10**: grid hiện đúng dữ liệu site, phân trang 2+1,
cross-site isolation đúng, SSO login hoạt động. [ADR-013](../../docs/adr/ADR-013-odata-cho-public-object-model.md)
chốt connector pattern; plan M4 C02 đã đóng.

## Dữ liệu và quyền

`make execution-prepare-poc` tạo schema qua DbUp và thêm 6 resource từ
`deploy/seed/factory-model.r3.json`: mỗi site có 3 resource thuộc MODULE/PACK.
Đây là WorkCell trong catalog, không tự thêm một tầng Equipment vào path.
Chạy lại không sửa row đã có hoặc đổi password. Chưa seed ProductionUnits/WipBoard của C03.

Runtime dùng role PostgreSQL `nvm_pom`, chỉ có `USAGE` schema `pom` và `SELECT` bảng Equipment.
Credential admin chỉ vào process migration/seed riêng. Site lấy từ token; EF query filter chạy trước
filter/paging/count của OData. Predicate đó là authorization; filter grid chỉ phục vụ hiển thị.

JWT yêu cầu issuer của realm `novavolt`, audience `nvm-api`, một claim `site_id` là NV1/DE1,
subject và role Operator/LineLeader trong `mendix_roles`. `nvm-mendix` cần mapper
`nvm-api-audience` như realm JSON. Sửa realm JSON **không** tự cập nhật realm đã import trong H2;
phải thêm mapper vào runtime hiện có và đăng nhập lại. Không recreate Keycloak khi chưa bảo toàn H2.
Mapper đã áp dụng vào runtime ngày 2026-09-09; token mới của `op.nv1`/`op.de1` có audience `nvm-api`.

## Chạy backend

Điền `NVM_POM_PASSWORD` trong `.env`; không dùng chung password migration. PostgreSQL và Keycloak
phải đang chạy. Khi dải port Windows bị giữ, xử lý theo [runbook](../../docs/runbook.md#port-windows-m4)
trước bước SSO. Các lệnh chạy từ repo root, trong Git Bash:

```sh
make execution-prepare-poc
make execution-up
```

Execution: `http://localhost:5081/pom/v1/`. Hai health endpoint ở `/health/live` và `/health/ready`;
readiness kiểm schema và quyền đọc. Docker runtime chỉ ở `it-net`, bind localhost, chạy non-root.
PoC dùng HTTP Keycloak local với `ASPNETCORE_ENVIRONMENT=Development`. Môi trường khác yêu cầu
issuer/discovery HTTPS và connection string tường minh; chưa tuyên bố môi trường Production đã được kiểm.

Host.All dùng cùng registration/controller/policy. Chạy local bằng:

```sh
dotnet run --project src/Apps/Nvm.Host.All
```

Migration ngoài fixture: chạy Execution với `--migrate` và truyền `NVM_POM__MigrationConnectionString`
qua môi trường. Startup web không tự migrate hay seed. M4/C02 chưa mở command ghi.

## Metadata và bước Mendix tiếp theo

[Equipment.metadata.xml](Equipment.metadata.xml) là snapshot response `$metadata` có auth của cùng
registration chạy bằng TestServer + PostgreSQL thật. Integration test so toàn bộ XML với snapshot này;
response từ Execution Docker dùng token Keycloak thật cũng đã được so khớp ngày 2026-09-09.
Ngày 2026-09-10, owner đã import snapshot thành `NvmShared.POM_v1` trên Studio Pro 11.12.3.
Service URL đã trỏ tới constant String `NvmShared.POM_ServiceUrl` với giá trị
`http://localhost:5081/pom/v1/`, không exposed to client. Owner báo 0 errors sau Save All/Check now;
`mx dump-mpr` xác nhận binding URL, OData4, `headerListMicroflow = NvmShared.SUB_Pom_CreateHeaders`
và `errorHandlingMicroflow = NvmShared.SUB_Pom_HandleError`. Owner đã thêm external entity
`NvmShared.Equipment`; MCP xác nhận source `POM_v1`, entity set `Equipment`, local `_Id` ánh xạ
remote `Id`. Access rule `NvmShared.User` đã cấp ReadOnly cho cả 7 thuộc tính, không Create/Delete,
default quyền thuộc tính mới là None; MCP xác nhận 0 lỗi model. App role Operator/LineLeader đã gán
`NvmShopFloor.User` và có role-based home page `NvmShopFloor.Equipment_PoC`. Dump model xác nhận
grid đọc `NvmShared.Equipment`, 7 cột, page size 2 và paging buttons; grid có auth ở runtime chưa kiểm.

App đang dùng: `C:\Users\Kingc\Mendix\NvmShopFloor-main\NvmShopFloor.mpr`, branch `main`, Studio Pro 11.12.3.
Owner đã xác nhận mở đúng app. Chưa sửa model Mendix bằng agent.

File đã import vào Consumed OData Service `NvmShared.POM_v1`. Tài liệu Mendix mô tả
menu `Add other > Consumed OData Service` và cách import file tại
[Importing from a File](https://docs.mendix.com/catalog/register/data-sources-without-mendix-cloud/).
Tiếp tục chạy app, đăng nhập và kiểm truy cập grid thật, phân trang và token refresh theo plan.

Studio Pro 11.12 dùng `Headers microflow` trả list `System.HttpHeader`; URL lấy từ constant.
Owner đã dựng `NvmShared.SUB_Pom_CreateHeaders` trong folder `Sso`; MCP xác nhận parameter
`HttpResponse` kiểu `System.HttpResponse`, return type `List<System.HttpHeader>` và 0 lỗi model.
`Required` yêu cầu truyền argument, vẫn cho phép giá trị `empty`.
Header Authorization phải lấy access token user hiện tại phía server. Không copy token vào constant,
entity nghiệp vụ, browser hoặc log.

Model OIDC 4.7.0 đã được đọc: `GetCurrentToken()` trả `OIDC.Token` của current user, chưa decrypt/refresh;
`OIDC.Decrypt` giải mã chuỗi; `GetNewAccessTokenUsingRefreshToken(Token, ClientConfiguration)` trả Boolean
và cập nhật/commit token tại chỗ. `GetAccessToken(HttpRequest)` đọc request vào, không dùng cho luồng này.
Model đã dựng: tạo list headers rỗng; lấy current token; khi callback có response 401,
lấy config qua `OIDC.Token_ClientConfiguration` và gọi refresh. Thiếu token/config/refresh token hoặc
refresh trả `false` thì trả list rỗng. Chỉ thêm `Authorization` sau khi decrypt ra access token không rỗng.
`Change list` thêm `$AuthorizationHeader` vào `$HttpHeaders`; header có Key `'Authorization'`,
Value `'Bearer ' + $AccessToken`. Service đã gán cả hai microflow. Error handler nhận `System.HttpResponse`,
trả `String`, xử lý response rỗng, 401, 403 và lỗi khác; MCP xác nhận 0 lỗi model.
Ngày 2026-09-10 owner xác nhận runtime: grid hiện đúng dữ liệu NV1 cho `op.nv1`, phân trang 2+1,
`op.de1` chỉ thấy DE1, SSO login qua `/oauth/v2/login` hoạt động, role-based home page đúng.
[Consumed OData Service — 11.12 and below](https://docs.mendix.com/refguide/consumed-odata-service/).

## Kiểm backend

```sh
dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj -- --filter-class '*PomEquipmentTests'
dotnet build src/Apps/Nvm.Host.All/Nvm.Host.All.csproj --nologo
```

Test dùng PostgreSQL 17.9 và JWT ký RSA với issuer test; không gọi Keycloak thật.
Các case kiểm signature/issuer/audience/lifetime, role/site, đổi principal qua cùng EF model cache,
filter/count/lookup, server paging 50+2, client paging, conditional ETag và từ chối write/query ngoài phạm vi.
Số đo hiện tại ở [benchmarks](../../docs/benchmarks.md).

Ngày 2026-09-09: 21/21 test POM và 23/23 architecture xanh. Kiểm thêm hai host đang chạy thật
(Execution 5081, Host.All 5080) với hai account NV1/DE1: `$count=3`, paging `2+1`, filter qua site khác
trả `0`, key site khác trả `404`, request không token trả `401`; metadata có auth trả `200`.
Host.All dùng cho kiểm chứng đã dừng; Execution Docker và Keycloak vẫn chạy cho PoC Mendix.
Ngày 2026-09-10: `make ci` xanh (21 POM + 23 architecture + unit/contract/analyzer). Mendix runtime
đã kiểm: grid, paging, cross-site, SSO login. ADR-013 chốt connector pattern. C02 đã đóng.

Package: OData 9.5.0, EF Core/Relational 10.0.11, Npgsql EF provider 10.0.3, JwtBearer 10.0.11.
Pin tại `Directory.Packages.props`; không viết parser OData hoặc framework connector riêng.
