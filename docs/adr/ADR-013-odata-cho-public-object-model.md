# ADR-013 — OData v4 cho Public Object Model, chứng minh bằng Mendix connector

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-10 |
| **Liên quan** | [scope.md](../scope.md) §5.3/§7.2/M4 · [plan M4](../plans/M4-mendix-operator-station.md) C02/C03 · [ADR-002](ADR-002-postgresql-timescaledb-read-model.md) · [ADR-014](ADR-014-mendix-ui-va-draft-ben-vung.md) · [ADR-038](ADR-038-data-collection-thu-cong-o-m4.md) |

## Context

Scope yêu cầu Public Object Model (POM) cung cấp read model cho UI Mendix: danh sách unit, WIP và
equipment theo site. Mendix 11.12 có Consumed OData Service nhưng không bảo đảm mọi third-party OData
service đều tương thích; connector có giới hạn về composite key, complex collection và navigation property.

C02 cần chứng minh **trước khi dựng màn hình** rằng pipeline ASP.NET Core OData → PostgreSQL → Mendix
grid hoạt động với auth, paging và site scope. Nếu thất bại, phải chốt cách tích hợp thay thế.

## Decision

### Stack và registration

Dùng ASP.NET Core OData 9.5.0 với EF Core Npgsql provider trên PostgreSQL read model (ADR-002).
Package pin tại `Directory.Packages.props`; không tự viết parser OData hoặc framework connector riêng.
Registration chung cho cả `Nvm.App.Execution` và `Nvm.Host.All`; controller/policy ở `Nvm.PublicObjectModel`.

### EDM và metadata

Key là `Edm.String` có `MaxLength` hữu hạn (64 cho Equipment). DTO phẳng, không navigation property,
không action/function. `$metadata` có auth; snapshot XML từ integration test dùng cho import vào Mendix
khi import trực tiếp từ URL không mang được token. Test so metadata response với snapshot đảm bảo
schema không lệch sau sửa code.

### Site scope

EF global query filter ép `SiteId` từ principal đã xác thực **trước** mọi `$filter`, `$count`, lookup
và paging. Key thuộc site khác trả 404, không tiết lộ sự tồn tại. Filter do client gửi không thay
predicate này. Đây là authorization, không phải tiện ích hiển thị.

### Paging và query

Server paging mặc định 50, `$top` tối đa 1.000; thứ tự ổn định có key phân xử. Trả `@odata.nextLink`
khi còn trang, bỏ ở trang cuối. `$expand`, `$apply` và query vượt `MaxTop` bị từ chối. Chỉ hỗ trợ
`$filter`, `$select`, `$orderby`, `$top`, `$skip`, `$count`. ETag cho entity đơn dùng `Revision`.

### Mendix connector pattern

Studio Pro 11.12.3 import metadata snapshot thành Consumed OData Service. URL trỏ tới constant,
không exposed to client. Hai microflow mở rộng:

- **Headers microflow** (`SUB_Pom_CreateHeaders`): lấy OIDC token của user hiện tại, decrypt,
  trả `Authorization: Bearer {token}`. Khi callback nhận 401, refresh token rồi thử lại.
  Không copy token vào entity nghiệp vụ, URL hay log.
- **Error handling microflow** (`SUB_Pom_HandleError`): xử lý response rỗng, 401, 403 và lỗi khác;
  trả message tiếng Việt cho người dùng.

External entity có access rule `ReadOnly` cho module role phù hợp; quyền thuộc tính mới mặc định None.

### Endpoint chỉ đọc

POST/PATCH/DELETE trả 405. POM không nhận command; command đi qua Command API riêng.

## Evidence — PoC C02

### Integration test (21/21 xanh, ngày 2026-09-09)

Testcontainers PostgreSQL 17.9, JWT ký RSA không cần Keycloak:

- Token thiếu/sai issuer/audience/hết hạn/sai chữ ký → 401
- Site rỗng, hai site, site không tồn tại, role không hợp lệ → 403
- Đổi principal giữa các request trên cùng EF model cache → site filter đúng theo principal mới
- `$count`, `$filter SiteId eq '{other}'` → 0; lookup key site khác → 404
- Server paging 50+2 (52 row/site), không gap/duplicate/foreign row; client `$skip/$top` đúng
- ETag conditional: 304 cho cùng site, 404 khi dùng ETag site khác
- Metadata khớp snapshot import; không navigation/action
- `$top=1001`, `$expand`, `$apply` → 400; POST/PATCH/DELETE → 405

### Keycloak token thật (ngày 2026-09-09)

Execution Docker (port 5081) và Host.All (port 5080), hai account `op.nv1`/`op.de1`:

- `$count=3` mỗi site; paging `2+1` row; filter qua site khác → 0; key site khác → 404
- Request không token → 401; metadata có auth → 200
- Audience mapper `nvm-api` đã áp dụng vào runtime Keycloak

### Mendix runtime (ngày 2026-09-10)

Studio Pro 11.12.3, OIDC 4.7.0, app `NvmShopFloor` branch `main`:

- Import metadata snapshot: 0 lỗi model; binding URL/OData4 đúng
- Headers và error handling microflow: 0 lỗi model
- Grid `Equipment_PoC` 7 cột, page size 2: hiện đúng dữ liệu NV1 cho `op.nv1`
- Phân trang: trang 2 hiện 1 row còn lại, quay lại trang 1 đúng
- Cross-site: `op.de1` chỉ thấy equipment DE1
- Login SSO qua `/oauth/v2/login` hoạt động; role-based home page đúng

## Consequences

**Được**

- Mendix connector hoạt động với third-party OData v4 service có auth, không cần anonymous endpoint.
- Pattern headers microflow + metadata snapshot tái sử dụng cho C03 khi thêm `ProductionUnits`/`WipBoard`.
- Site scope là authorization ở backend; Mendix grid chỉ hiển thị dữ liệu đã được lọc.
- Package pin và metadata snapshot test giúp phát hiện breaking change khi nâng package.

**Mất / phải chịu**

- Metadata phải re-import trong Studio Pro khi schema thay đổi (thêm entity set, đổi property).
  Không có cách tự động đồng bộ schema giữa hai repo.
- Connector không hỗ trợ `$expand` hay navigation; join phải làm ở UI hoặc flatten ở backend.
  DTO phẳng đủ cho M4; milestone sau cần đánh giá lại nếu muốn nested data.
- Token refresh logic nằm trong headers microflow; nếu refresh token hết hạn, user phải re-login.
  Không có cơ chế tự chuyển về trang login khi cả refresh lẫn access đều hết.
- ETag dùng `Revision` column; entity không có Revision phải chọn cách khác hoặc bỏ conditional request.

**Việc phát sinh:** C03 thêm hai entity set, re-import metadata; dùng cùng pattern controller/policy/test.
Nếu nâng Studio Pro hoặc OIDC module, kiểm lại headers microflow và connector behavior.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| REST API thuần với JSON, Mendix gọi qua Call REST | Mất paging/filtering/metadata contract; mỗi màn hình phải viết mapping riêng |
| Mở endpoint OData anonymous, auth chỉ ở Mendix | Vi phạm K3; bất kỳ ai có URL đều đọc được dữ liệu sản xuất |
| GraphQL | Mendix không có connector GraphQL tích hợp; thêm thư viện và viết schema riêng |
| Tự viết OData parser / framework connector | Tốn effort, dễ lệch spec; ASP.NET Core OData đã xử lý query translation |
| Import metadata từ URL trực tiếp | URL có auth nhưng Studio Pro không truyền token khi import; snapshot file là workaround đã chứng minh |
