# Keycloak realm — `novavolt`

Chú thích cho `realm-novavolt.json` nằm ở đây, **không** nằm trong file JSON.

> [!danger] Không được chú thích trong realm JSON
> Keycloak deserialize bằng Jackson ở chế độ **strict**: một trường lạ làm import **chết
> hẳn**, không phải bỏ qua. Thêm `"_comment": "..."` cho dễ đọc sẽ cho ra:
>
> ```
> ERROR: Failed to run import
> ERROR: Unrecognized field "_comment_roles" … not marked as ignorable
> ```
>
> Container vẫn khởi động lại liên tục và healthcheck báo unhealthy — triệu chứng không
> hề gợi ý rằng nguyên nhân là một dòng chú thích.

---

## Realm được nạp thế nào

`docker-compose.yml` mount thư mục này vào `/opt/keycloak/data/import` (chỉ đọc) và chạy
Keycloak với `start-dev --import-realm`.

**Không có volume cho H2.** Đây là quyết định thiết kế, không phải thiếu sót:

- Mỗi lần recreate mà không khôi phục H2 là một lần import lại từ file → `realm-novavolt.json` là **nguồn sự
  thật duy nhất**.
- Mọi thay đổi bấm tay trong Admin Console **biến mất khi recreate mất H2**, buộc thay đổi phải đi
  qua file này và được commit. Realm-as-code.
- Cũng tránh luôn lỗi kỹ thuật: named volume gắn vào `/opt/keycloak/data/h2` được Docker
  tạo với chủ sở hữu `root`, còn Keycloak chạy bằng uid 1000, nên H2 chết với
  `AccessDeniedException` ngay lúc khởi động.

`docker restart` giữ writable layer và H2; realm đã tồn tại sẽ không được import đè.
Chốt mapper/role vào JSON và áp dụng cùng thay đổi vào realm đang chạy. Trước khi recreate container
đang dùng cho Mendix, phải giữ database/cấu hình hiện có; không dùng recreate như một cách cập nhật mapper.

> [!warning] `docker compose restart` KHÔNG nạp lại realm
> "Mỗi lần khởi động là một lần import lại" chỉ đúng với **container mới**. H2 nằm trong lớp
> ghi của container, không nằm trong volume — nên `restart` giữ nguyên database cũ và Keycloak
> **bỏ qua** import vì realm đã tồn tại. Không có cảnh báo nào; realm chỉ đơn giản là cũ.
>
> Sau khi sửa `realm-novavolt.json`, phải **tạo lại container**:
>
> ```bash
> docker compose up -d --force-recreate keycloak
> ```
>
> Đã gặp thật: thêm mapper `mendix-roles`, `restart`, lấy token — claim không có. Cùng file đó,
> `--force-recreate`, lấy token — claim có.

---

## Nội dung

### Vai trò (realm role)

| Role | Dùng cho |
|---|---|
| `Operator` | Vận hành máy, quét serial, nhập kết quả đo |
| `LineLeader` | Trưởng chuyền; được quyền đặt hold |
| `QaEngineer` | Điều tra NCR |
| `QaManager` | Ký duyệt MRB |
| `ProductionManager` | Duyệt scrap |
| `ComplianceOwner` | Hồ sơ DPP và tuân thủ |
| `Admin` | Quản trị hệ thống |

Mendix ánh xạ realm role sang **user role** trùng tên — xem mục
[`mendix_roles`](#mendix_roles--vì-sao-realm_accessroles-không-dùng-được) bên dưới. Lưu ý bẫy ở
[`studio-pro-traps.md`](../../.claude/skills/mendix-manual/references/studio-pro-traps.md) §4:
Published REST hiển thị module role nhưng runtime phân quyền theo user role.

### Client

| Client | Dùng cho | Flow |
|---|---|---|
| `nvm-api` | Service .NET gọi lẫn nhau | Service account |
| `nvm-mendix` | OIDC SSO module của Mendix | Authorization code |

Cả hai bật `directAccessGrantsEnabled` để lấy token bằng `curl` lúc kiểm chứng.
**Production phải tắt.**

Secret là `dev-only-not-a-secret` — cố ý đặt tên như vậy để không ai nhầm nó là bí mật thật.

### User test

| Username | Password | Role | `site_id` |
|---|---|---|---|
| `op.nv1` | `dev` | Operator | `NV1` |
| `qa.nv1` | `dev` | QaEngineer | `NV1` |
| `op.de1` | `dev` | Operator | `DE1` |

`op.de1` tồn tại riêng để kiểm **cross-site isolation** ở M10: user thuộc NV1 không được
thấy bất kỳ dòng dữ liệu DE1 nào, ở mọi endpoint.

Tài khoản `ll.nv1` có role `LineLeader`, `site_id=NV1`, dùng kiểm Dispatch/Scan M4/C04.
Realm JSON khai báo danh tính và role, không chứa credential của tài khoản này.
Mật khẩu đã được cấp riêng trên realm local đang chạy; khi import vào môi trường mới,
cấp lại mật khẩu qua Admin Console hoặc Admin API từ nguồn credential được phép dùng.
Trong M4, LineLeader cùng quyền thao tác trong site với Operator, chưa có chức năng đặt hold,
release hay override trên hai màn hình này.

---

## `mendix_roles` — vì sao `realm_access.roles` không dùng được

Keycloak đặt realm role vào access token ở **`realm_access.roles`**, tức là một mảng **lồng
trong một object**. Module OIDC SSO của Mendix đọc claim bằng
`JSONObjectUtils.getStringList(claims, name)` — hàm này chỉ lấy được **mảng chuỗi ở cấp cao
nhất** của payload. Đưa `realm_access` vào thì nó ném `ParseException`; đưa
`realm_access.roles` thì không có claim nào tên như vậy.

Nên client `nvm-mendix` có thêm mapper `mendix-roles` (`oidc-usermodel-realm-role-mapper`,
`multivalued: "true"`) sinh ra claim **phẳng**:

```json
"mendix_roles": ["Operator"]
```

Microflow `NvmShared.CustomATP_KeycloakRealmRoles` trong app Mendix đọc đúng claim này rồi
so tên với user role trong app. **Tên phải trùng chính xác, phân biệt hoa thường** — realm
role `Operator` chỉ khớp user role Mendix tên `Operator`. Role nào trong token mà app không
có thì bị bỏ qua, không báo lỗi.

`usermodel.realmRoleMapping.rolePrefix` để **rỗng** có chủ ý: thêm tiền tố là phá vỡ phép so
tên đó.

Mapper cũ `site-id` vẫn giữ nguyên và độc lập — nó đi đường khác (attribute mapping của JIT
provisioning), không qua microflow này.

---

## `site_id` và User Profile — bẫy thứ hai

Từ bản 24, Keycloak bật **User Profile** mặc định và **âm thầm loại bỏ** mọi attribute
không được khai báo. Nếu chỉ viết `"attributes": {"site_id": ["NV1"]}` trên user mà không
khai báo `site_id` trong user profile thì:

- import **thành công**, không cảnh báo gì;
- user hiện ra bình thường trong console;
- nhưng token **không bao giờ** có claim `site_id`.

Vì vậy realm JSON có khối `components → org.keycloak.userprofile.UserProfileProvider`,
trong đó khai báo `site_id` và đặt `unmanagedAttributePolicy: ENABLED`.

Giá trị của `kc.user.profile.config` là **một chuỗi JSON lồng trong JSON**. Sửa nó thì
kiểm lại bằng:

```bash
python -c "import io,json;d=json.load(io.open('deploy/keycloak/realm-novavolt.json',encoding='utf-8'));json.loads(d['components']['org.keycloak.userprofile.UserProfileProvider'][0]['config']['kc.user.profile.config'][0]);print('ok')"
```

---

## Kiểm chứng sau khi đổi realm

Không tin vào việc "import không báo lỗi". Lấy token thật rồi giải mã payload:

```bash
curl -s -d "client_id=nvm-mendix" -d "client_secret=dev-only-not-a-secret" \
     -d "username=op.nv1" -d "password=dev" -d "grant_type=password" \
     http://localhost:8081/realms/novavolt/protocol/openid-connect/token
```

Trong payload phải thấy **cả ba**: `realm_access.roles` (Keycloak sinh mặc định),
`mendix_roles` (mảng phẳng cho Mendix), và `site_id` đúng giá trị. Thiếu `mendix_roles` thì
Mendix vẫn đăng nhập được nhưng mọi người dùng chỉ nhận user role mặc định.

---

## Cổng 8081, không phải 8080

Mendix Studio Pro chạy app local ở 8080 và đổi nó phiền hơn. Quyết định chốt ở C05, ghi
trong `.env.example`.

Lưu ý riêng: **health endpoint của Keycloak 26 nằm ở management port 9000 trong container**,
không phải 8080 — và image không có `curl` lẫn `wget`, nên healthcheck phải mở socket bằng
bash.
