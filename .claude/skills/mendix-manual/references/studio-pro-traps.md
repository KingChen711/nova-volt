# Bẫy Studio Pro đã được kiểm chứng

§0 là những gì **kiểm chứng trực tiếp trong dự án NovaVolt MES**. Từ §1 trở đi là ledger kế
thừa từ dự án CGVibe. Cả hai đều là thứ **đã tái lập được**, không phải phỏng đoán.

---

## 0. Kiểm chứng trong NovaVolt MES — Studio Pro 11.12.3

> Bản Studio Pro thực tế là **11.12.3**, không phải 11.12.1 như plan M0 ghi.
> App `NvmShopFloor` dùng **Git**, không phải SVN. `mx.exe` cho `mx check` phải đúng 11.12.3.

### 0.1 Tên menu đã xác nhận

| Việc | Đường dẫn đúng |
|---|---|
| Mở Marketplace | **View → Marketplace** |
| Sửa lỗi `CE6087` | Right-click dòng lỗi → **Update all renamed design properties in project** |
| Bật anonymous users | **App → Security → tab Anonymous users** |
| After-startup microflow | **App → Settings → tab Runtime → After startup** |

> [!warning] `CE6087` — tên mục KHÔNG phải "Convert"
> Nhiều bài viết cũ (thời Mendix 7→8) ghi là "convert design property values". Studio Pro 11
> đặt tên khác hẳn: **Update all renamed design properties in project**. Menu chuột phải ở
> Studio Pro 11 còn có `Maia Explain` và `Solve problem with Maia Chat` — hai mục AI, không
> phải thứ cần dùng ở đây.

### 0.2 `CE6087` xuất hiện sau MỖI lần cài module có themesource

Không phải sự cố một lần. Cứ cài module Marketplace mang theo `design-properties.json` là lỗi
này quay lại. Đã gặp hai lần liên tiếp: một lần sau khi cài Community Commons + User Commons,
một lần nữa sau khi cài OIDC SSO.

Cách xử lý luôn giống nhau và an toàn, **miễn là app chưa có design property tự viết**.

### 0.3 OIDC SSO 4.7.0 — dependency thật

Tài liệu liệt kê 6 module, nhưng ba trong số đó có điều kiện version. Với bản **4.7.0** trên
Mendix 11, chỉ cần **ba**:

| Module | Bắt buộc? |
|---|---|
| Community Commons | ✅ |
| User Commons | ✅ |
| Events | ❌ **tài liệu ghi cần, thực tế không** — xem §0.11 |
| Encryption | ❌ chỉ cho OIDC ≤ 4.3.0 |
| Nanoflow Commons | ❌ chỉ cho ≤ 4.3.0 (và Blank Web App đã có sẵn) |
| Mx Model Reflection | ❌ chỉ cho ≤ 4.2.1 |

**Cài dependency TRƯỚC OIDC SSO.** Ngược lại thì Error List đỏ hàng loạt vì thiếu tham chiếu,
và phải lần ngược từ thông báo lỗi để đoán module nào còn thiếu.

### 0.4 Constant encryption key đổi chỗ theo version

| Version OIDC | Constant |
|---|---|
| ≥ 4.4.0 | `OIDC.Encryptionkey` — nằm trong chính module OIDC |
| ≤ 4.3.0 | `Encryption.EncryptionKey` — nằm trong module `Encryption` |

Dấu hiệu nhanh: app **không** có module `Encryption` nghĩa là đang dùng bản mới.

### 0.5 Blank Web App mang sẵn 8 module

`themesource` sau khi tạo app cho biết template đã kèm gì — đọc nhanh hơn mở App Explorer:
`administration`, `atlas_core`, `atlas_web_content`, `datawidgets`, `feedbackmodule`,
`myfirstmodule`, `nanoflowcommons`, `webactions`.

**Không** có `communitycommons`, `usercommons`, `encryption`.

### 0.6 Navigation profile 11.12.3 — các mục thực có

Profile **Responsive web** hiển thị theo thứ tự: **General** · **Home pages** (Default home page,
Role-based home pages, Fallback page) · **Progressive Web App** · **Menu**.

Mục **Authentication** (nơi đặt *Sign-in page*) **chỉ xuất hiện khi
`Allow anonymous users = Yes`**. Đã xác nhận: trước khi bật, profile chỉ có 4 mục trên; bật
xong thì Authentication hiện ra.

Tài liệu `docs.mendix.com/refguide/navigation/` mô tả mục này nhưng **không** nêu điều kiện
hiển thị — nên đừng kết luận "Studio Pro thiếu tính năng" khi không thấy nó.

### 0.7 `CE0157` — user role KHÔNG được rỗng module role

> *User role should have at least one module role from another module than System.*

Mendix bắt **mọi** user role phải có ít nhất một module role từ module **không phải System**.
Nên ý tưởng "role `Anonymous` rỗng hoàn toàn cho an toàn" là **không hợp lệ**.

Cách đúng vẫn giữ được mức quyền bằng không: tạo một **module role rỗng access rule** trong
module của chính app, rồi gán nó.

1. App Explorer → module `MyFirstModule` → double-click **Security**
2. Tab **Module roles** → **New** → tên `Anonymous`
3. Không thêm entity access, page access hay microflow access nào
4. **App → Security → User roles** → `Anonymous` → **Edit** → tích `MyFirstModule.Anonymous`

Kết quả: CE0157 hết, mà anonymous vẫn không xem được gì. Module role tồn tại nhưng **rỗng
quyền** — khác hẳn với việc gán một module role có sẵn như `OIDC.User`, thứ sẽ mở quyền thật.

> Mỗi module có document **Security** riêng, thấy được trong App Explorer ngay dưới tên module.

### 0.8 MỖI user role mới là một DÂY CHUYỀN, không phải một dòng trong bảng

Ban đầu ghi mục này cho riêng `Anonymous`. Sau đó tạo `Operator` và `LineLeader` thì **đúng
`CE2729` đó quay lại y hệt** — nên đây không phải chuyện của anonymous, mà là chuyện của **mọi
user role mới**.

> Checklist khi thêm một user role: tích module role của **module nghiệp vụ** cần dùng, **cộng
> thêm `Atlas_Core.User`**. Thiếu cái thứ hai thì `CE2729` nổ ngay, và nó nổ ở một document
> hoàn toàn không liên quan tới việc bạn đang làm.

Khoảnh khắc một user role mới tồn tại, consistency checker kiểm **tĩnh** mọi thứ role đó
có thể chạm tới. Thứ tự lỗi đã gặp, mỗi lần sửa xong lại lòi ra cái tiếp:

1. `CE0157` — user role rỗng module role → tạo module role rỗng quyền trong `MyFirstModule`
2. `CE2729` — `No access to microflow 'Atlas_Core.DS_Account_CurrentUser'` → cấp `Atlas_Core.User`

`CE2729` đến từ snippet **LanguageSelector** nằm trong layout dùng chung: layout → snippet →
DataView → data source là microflow của `Atlas_Core`.

Đây là kiểm tra **tĩnh**, không phải runtime. Thực tế anonymous bị đá sang IdP ngay và không
bao giờ render nổi snippet đó, nhưng checker chỉ thấy một đường đi khả dĩ thiếu quyền.

**Nguyên tắc xử lý**: anonymous cần đủ quyền để dựng **cái vỏ**, không hơn.

| Module bị nhắc tên | Xử lý |
|---|---|
| Module giao diện (`Atlas_Core`, `Atlas_Web_Content`) | Cấp module role tương ứng — an toàn, không chứa entity nghiệp vụ |
| Module nghiệp vụ (`Administration`, `OIDC`, module của app) | **Không cấp.** Gỡ thứ đó khỏi đường đi của anonymous |

`Atlas_Core.User` an toàn vì `Atlas_Core` thuần giao diện; `DS_Account_CurrentUser` chỉ trả về
tài khoản đang đăng nhập để hiện tên, và với anonymous nó trả rỗng.

**Vì sao nó luôn tìm ra bạn**: snippet `LanguageSelector` nằm trong layout `Atlas_TopBar` — layout
mà gần như mọi trang đều dùng. Bất kỳ role nào mở được **một** trang nào đó là đã đi qua layout
đó, nên `CE2729` là lỗi gần như chắc chắn với mọi user role mới. Đừng đợi nó; tích
`Atlas_Core.User` ngay lúc tạo role.

### 0.9 Đọc security level: MCP không làm được, `mx dump-mpr` làm được

`Security$ProjectSecurity` **không phải** document type của MCP — đã xác nhận trên 11.12.3:

```
ped_read_document(Security$ProjectSecurity)  →  ERROR: Unknown document type
```

`mx export-security-overview` cũng **không** trả về security level (chỉ có `entityAccess`,
`documentAccess`, `userRoles`). Cách duy nhất đọc được:

```bash
mx dump-mpr --unit-type='Security$ProjectSecurity' --output-file <out.json> <app.mpr>
```

> File xuất ra có **UTF-8 BOM** — đọc bằng `encoding='utf-8-sig'`, nếu không `json.load` báo
> *"Unexpected UTF-8 BOM"*.

Các khoá đáng đọc: `securityLevel`, `checkSecurity`, `enableDemoUsers`, `enableGuestAccess`,
`guestUserRoleName`, `adminUserName`.

### 0.10 `enableDemoUsers` KHÁC security level — đừng suy ngược

Bảng chọn `demo_administrator` / `demo_user` ở cuối trang **không** chứng minh app đang ở
Prototype. Đã đo trên app này:

| Khoá | Giá trị |
|---|---|
| `securityLevel` | `CheckEverything` ← chính là **Production** |
| `enableDemoUsers` | `True` ← thứ tạo ra bảng chọn |
| `enableGuestAccess` | `True` |

**`CheckEverything` là tên nội bộ của mức Production.** Đừng tìm chuỗi `"Production"` trong dump.

Demo users là tính năng **độc lập**, chạy song song với Production.

> [!warning] Sửa: demo user KHÔNG có trên trang `login.html`
> Bản đầu của mục này viết *"bấm `demo_administrator` để có role Administrator mà không cần mật
> khẩu MxAdmin"* — ngầm hiểu là bấm trên trang đăng nhập của app. **Sai.** Đã kiểm trực tiếp
> trên app đang chạy, `enableDemoUsers = True`, pane hiển thị bình thường, đã reload:
>
> - Trang `http://localhost:8080/login.html` chỉ có **User name · Password · Sign in**.
> - `curl http://localhost:8080/js/login.js` — toàn bộ file **không có** một chữ `demo` nào,
>   chỉ có `action: "login"`.
>
> Tức là trang đăng nhập của Mendix 11 không bao giờ dựng bảng chọn demo user, bất kể
> `enableDemoUsers`. Vào bằng trình duyệt thì **chỉ có `MxAdmin` + mật khẩu đặt lúc F5 đầu tiên**
> — mật khẩu đó không nằm trong repo và không nằm trong `.launch` (ở đó `ADMIN_PASS` chỉ là cờ
> `1`, không phải giá trị).
>
> Bảng chọn demo user nằm ở đâu thì **chưa xác định** — nhiều khả năng là affordance của chính
> Studio Pro (View App), không phải của app. Chưa thấy tận mắt nên không ghi tên menu ở đây.

**Lối thoát vòng trứng-gà vẫn còn — nhưng nó là mật khẩu trong model, không phải cái nút.**
Demo user được lưu **nguyên văn** trong `Security$ProjectSecurity`, đọc được không cần đăng nhập:

```bash
mx dump-mpr --unit-type='Security$ProjectSecurity' --output-file sec.json <app.mpr>
```

```json
"demoUsers": [
  { "userName": "demo_administrator", "password": "<12 ký tự>", "entity": "Administration.Account" },
  { "userName": "demo_user",          "password": "<12 ký tự>", "entity": "Administration.Account" }
]
```

Gõ cặp đó vào `login.html` như tài khoản thường. Mật khẩu do Studio Pro sinh, khớp sẵn password
policy của app (ở app này: `minimumLength 12`, `requireMixedCase`, `requireDigit`).

Hai chỗ dễ nhầm trong cùng file dump:

| Khoá | Là gì |
|---|---|
| `demoUsers[].password` | **Mật khẩu thật**, dùng đăng nhập được ngay |
| `adminPassword` | **Cờ**, không phải giá trị. Ở app này bằng `1`, không thoả nổi chính password policy của nó |

`M2EE_ADMIN_PASS=1` trong `.launch` cũng là cờ — thử xác thực vào admin port `8090` bằng `1` trả
`{"result":-4,"message":"Authentication failed."}`.

Quên mật khẩu `MxAdmin` thì đặt lại ở **App → Security → Administrator**
([support.mendix.com](https://support.mendix.com/hc/en-us/articles/17680178997404-How-to-change-the-MxAdmin-password-for-an-application-published-to-the-Mendix-Free-Cloud) —
tài liệu chính thức, chưa tự mắt kiểm tên tab trên 11.12.3), rồi chạy lại app. Nhưng nếu chỉ cần
một phiên Administrator để cấu hình OIDC thì dùng `demo_administrator` nhanh hơn hẳn.

### 0.11 OIDC 4.7.0 KHÔNG cần module `Events`

Tài liệu `docs.mendix.com/appstore/modules/oidc/` liệt kê `Events (v4.0.0+ for Mendix 10)` là
dependency. Thực tế trên **Mendix 11 + OIDC 4.7.0**: `list_modules` không hề có `Events`, mà app
vẫn **0 lỗi**.

Dependency thật sự chỉ có **hai**: `CommunityCommons`, `UserCommons`.

*(Sửa lại §0.3 — mục đó ghi ba module, dựa vào tài liệu chứ chưa đo.)*

### 0.12 Thiếu một module role: menu biến mất, Error List vẫn 0

Đã gặp thật. Mục menu **IdP configuration** không hiện ra, trong khi:

- mục đó **có** trong model (đọc Navigation xác nhận), trỏ đúng `OIDC.OIDC_Client_Overview`
- mục **Accounts** ngay cạnh, trỏ `Administration.Account_Overview`, hiện bình thường
- Error List: **0 lỗi**
- `mx check`: sạch

Nguyên nhân: user role `Administrator` thiếu module role `OIDC.Administrator`. Không có gì báo,
vì *một user role không có module role của module X* **không phải lỗi model** — nó chỉ có nghĩa
là role đó không dùng module X.

**Cách chẩn đoán, dùng lại được cho mọi lỗi "menu/trang không hiện":**

```bash
mx export-security-overview -t json -o sec.json <app.mpr>
```

Đọc bằng `encoding='utf-8-sig'`. Hai chỗ cần nhìn:

| Khoá | Cho biết |
|---|---|
| `userRoles[].moduleRoles[]` | user role X đang có những module role nào |
| `documentAccess[]` — lọc theo `document` | trang đó **user role nào** với tới được |

`documentAccess` trả về **danh sách rỗng** cho một trang nghĩa là: trang có gán module role,
nhưng **không user role nào** ánh xạ tới module role đó. Đây là chữ ký của lỗi này.

So sánh cạnh nhau là cách nhanh nhất:

```
Administration.Account_Overview  -> ['Administrator']   ← hiện
OIDC.OIDC_Client_Overview        -> []                  ← không hiện
```

> Bản export đọc file `.mpr` **đã lưu**. Nếu vừa sửa security trong Studio Pro mà chưa
> **Save All**, nó trả lời về model cũ.

### 0.13 Cấu hình OIDC là wizard 4 bước, không phải form phẳng

Trang **IdP configuration** → **New** mở ra wizard:

| Bước | Nội dung | Thực tế phải làm |
|---|---|---|
| 1. Endpoints | Dán well-known URL → **Import IdP-metadata** | Tự điền 7 endpoint còn lại |
| 2. General Configuration | Client ID, secret, PKCE, callback, scopes | Chỗ duy nhất phải gõ nhiều |
| 3. Creating Users | JIT provisioning, attribute mapping | **Mặc định đã đúng**, bấm Next |
| 4. Assigning Roles | Token selection, microflow gán role | Để trống microflow là đủ để chạy |

Sau **Save and Finish**, IdP vẫn **chưa dùng được**: phải quay lại danh sách, chọn dòng đó rồi
bấm **Toggle Active** *và* **Make Default**. Thiếu `Default` thì `/oauth/v2/login` không biết
đẩy người dùng đi đâu.

Bổ sung sau khi tự chạy lại toàn bộ wizard qua trình duyệt (2026-08-26):

- **Trước bước 1 còn một hộp thoại nữa**: `Edit` mở *"Configure a SSO connection"* hỏi
  **Configuration name (Alias)** → **Next** mới vào Endpoints.
- **`Edit` xong rồi `Save and Finish` KHÔNG làm mất Active/Default.** Đã kiểm: sau khi lưu,
  dòng đó vẫn `Yes/Yes`.
- **Microflow ATP tự viết hiện đúng trong dropdown bước 4** miễn là tên có chứa `CustomATP` —
  `NvmShared.CustomATP_KeycloakRealmRoles` nằm cùng danh sách với bốn microflow của module.

### 0.13.1 Combobox rỗng nhưng LOOKS đã chọn — và tài liệu sai về principal attribute

Hai cái bẫy nối nhau, đều gặp thật ở bước **Creating Users**.

**Một: giá trị bạn thấy có thể là placeholder.** Mở `Edit` lại một cấu hình đang chạy tốt, các
combobox hiện `NvmShared.NvmAccount`, `email`, `OIDC.OIDC_CustomUserParsing_Standard` — trông
như đã chọn. Thực tế **rỗng**. Phân biệt bằng DOM, không bằng mắt:

```js
[...document.querySelectorAll('.widget-combobox-placeholder-text')]
  .map(el => ({txt: el.textContent.trim(), cls: el.className}))
```

| Class | Nghĩa |
|---|---|
| `widget-combobox-placeholder-text` | **Đã có giá trị** |
| `widget-combobox-placeholder-text widget-combobox-placeholder-empty` | **Rỗng**, chữ đang hiện chỉ là gợi ý |

Dấu hiệu nhìn được: ô đã có giá trị mới có nút **×** để xoá.

**Hai: tài liệu Mendix nói ngược với thực tế.** `docs.mendix.com/appstore/modules/oidc/` viết:

> *"You cannot use the IdP claim, which is the primary attribute identifying the user, and you
> cannot use the attribute you set in The attribute where the user principal is stored."*

Thực tế trên OIDC 4.7.0: wizard **bắt buộc** phải map nó. Bấm **Next** ở bước 3 mà thiếu thì ra
hai hộp lỗi nối tiếp:

```
Invalid userprovisioning configuration
Map Principal attribute to any one of attribute of userclaims
```

Hộp **thứ hai** mới là cái nói đúng việc phải làm. Hộp thứ nhất vô nghĩa nếu đọc một mình.

Cấu hình chạy được, đã kiểm chứng — bốn dòng mapping, **kể cả `sub` → `Name`**:

| IdP Attribute | Configured Entity Attribute |
|---|---|
| `sub` | `Name` ← principal, tài liệu bảo đừng map, thực tế bắt buộc |
| `name` | `FullName` |
| `email` | `Email` |
| `site_id` | `SiteId` |

Kèm: *Custom user entity* = `NvmShared.NvmAccount`, *The attribute where the user principal is
stored* = `Name`, *User metering named identifier* = `email`, *Default user role* = `User`.

**Claim lạ tạo ở đâu**: trong hộp *Add Claim Map* bấm **Search** → hộp *"Claims for claim entity
attribute"* → **New** → *"New Attribute"* điền **Claim Name** và **Friendly Name** → **Save** →
cuộn xuống cuối danh sách, chọn dòng vừa tạo → **Select**.

### 0.14 `form_post` chạy tốt với Keycloak — đã kiểm chứng

Form gợi ý *"query is most common, but form_post should be preferred for security reasons"*.
Đã chọn `Form post` và **đăng nhập thành công đầu-cuối** với Keycloak 26.7.2.

Authorization request sinh ra (đọc bằng `location.href` trên trang Keycloak):

```
response_type=code&response_mode=form_post&client_id=nvm-mendix
&redirect_uri=http%3A%2F%2Flocalhost%3A8080%2Foauth%2Fv2%2Fcallback
&scope=openid%20profile%20email
&code_challenge_method=S256&code_challenge=...
```

Không cần sửa gì bên Keycloak: realm khai `redirectUris: http://localhost:8080/*` đã bao trùm
`/oauth/v2/callback`.

**Cách chẩn đoán khi SSO hỏng**: mở console trên trang đăng nhập của IdP và đọc `location.href`.
Toàn bộ tham số Mendix gửi đi nằm ở đó — sai client_id, sai redirect_uri, thiếu PKCE đều lộ ra
ngay, không phải đoán.

### 0.15 JIT provisioning: Login là UUID, FullName mới là tên người

Sau lần đăng nhập đầu của `op.nv1`, trang **Accounts** hiện:

| Full name | Login | Roles |
|---|---|---|
| `Operator NV1` | `f9ef2039-2a57-4821-879b-d9c34af197de` | `User` |

Vì mapping mặc định ở bước 3 là `sub` → `Name`, mà `sub` của Keycloak là UUID. **Đây là hành vi
đúng**, không phải lỗi cấu hình:

- `sub` **bất biến** — đổi username, email, tên trong IdP thì nó vẫn nguyên
- `preferred_username` **đổi được** — dùng làm khoá thì một lần đổi tên là mất liên kết tài khoản
  và toàn bộ lịch sử thao tác của người đó

Với hệ traceability phải quy trách nhiệm về đúng một người trong 15 năm, đây là lựa chọn đúng.
**Mọi UI hiển thị cho người đọc phải dùng `FullName`**, không dùng `Name`.

`site_id` **không** tự động được lưu. Claim có trong access token nhưng không có gì trích nó ra —
cần microflow `UC_CustomProvisioning` (bước 3) hoặc microflow xử lý access token (bước 4).

### 0.16 Realm role của Keycloak KHÔNG vào thẳng được Mendix

Cấu hình "Custom AccessToken processing microflow" ở bước 4 của wizard chỉ chạy được với claim
là **mảng chuỗi ở cấp cao nhất** của payload. Đọc từ mã nguồn module trong `javasource/`:

```
OIDC.AzureRoleParse  →  RoleAssignmentUtil.getIdpRolesFromToken(ctx, token, claimName)
                     →  TokenUtils.getClaimValueAsStringArray(token, name)
                     →  JSONObjectUtils.getStringList(claimsSet.getClaims(), name)
```

Keycloak lại đặt realm role ở **`realm_access.roles`** — mảng **lồng trong object**. Hệ quả:

| Truyền vào `ClaimName` | Kết quả |
|---|---|
| `realm_access` | `ParseException` — giá trị là object, không phải mảng chuỗi |
| `realm_access.roles` | Không có claim nào tên như vậy ở cấp cao nhất → rỗng |

`OIDC.GetClaimValue` cũng vô dụng ở đây: nó gọi `getStringClaim`, chỉ đọc được chuỗi.

**Cách đúng: sửa phía Keycloak, không sửa phía Mendix.** Thêm mapper
`oidc-usermodel-realm-role-mapper` với `multivalued: "true"` để sinh claim phẳng
`mendix_roles: ["Operator"]`. Xem `deploy/keycloak/README.md`.

Rồi microflow tự viết chỉ cần sao chép nguyên hình dạng của
`OIDC.Default_Azure_TokenProcessing_CustomATP` — **5 node, không có vòng lặp**:

```
Start → Retrieve (database, System.UserRole, xpath rỗng) → Java action OIDC.AzureRoleParse
      → End (trả $RolesFromToken)
```

`AzureRoleParse` nhận `ClaimName` làm **tham số**, nên nó không hề riêng cho Azure — chỉ cần
truyền `'mendix_roles'` là dùng cho Keycloak. Java action so khớp bằng
`userRole.getName().equals(...)` **hoặc** `getModelGUID()`, nên tên user role Mendix phải trùng
tên realm role **chính xác, phân biệt hoa thường**. Role không khớp thì bị bỏ qua **im lặng**.

> Tên microflow **phải chứa chuỗi `CustomATP`**, nếu không nó không hiện trong dropdown —
> `OIDC.GetCustomATPList` lọc bằng `microflowName.contains("CustomATP")`.

### 0.17 Claim lạ (`site_id`) đi bằng custom user entity, không bằng microflow

Tài liệu Mendix mô tả hai đường cho JIT provisioning. Đường **ngắn** là đủ cho claim thường:

1. Tạo entity kế thừa `Administration.Account` (ví dụ `NvmShared.NvmAccount`) với attribute
   cần lưu (`SiteId`).
2. Trong app đang chạy: **IdP configuration → Creating Users** → *Custom user entity* đổi từ
   `Administration.Account` sang entity mới → **Attribute Mapping** thêm `site_id` → `SiteId`.

Không cần microflow `UCCustomProvisioning` — đường đó chỉ dành cho user entity **không** kế
thừa `System.User`.

Claim phải khai `id.token.claim` hoặc `userinfo.token.claim` phía IdP thì attribute mapping mới
thấy. Claim nào không có trong `claims_supported` của well-known thì tạo tay: **Add Claim →
Search → New**.

**Với realm `novavolt` thì chắc chắn phải tạo tay.** `claims_supported` của Keycloak chỉ liệt kê
claim chuẩn OIDC:

```
iss sub aud exp iat auth_time name given_name family_name preferred_username email acr azp nonce
```

`site_id` và `mendix_roles` **không** nằm trong đó, dù cả hai đều thật sự có trong token. Keycloak
không quảng cáo protocol mapper tự viết ở well-known — nên dropdown IdP Attribute sẽ trống chỗ
đó, và đó **không** phải dấu hiệu claim bị thiếu.

> [!warning] Đổi custom user entity khi đã có user — chưa kiểm chứng, nhưng phải lường trước
> `System.User.Name` có ràng buộc duy nhất trên **toàn bộ** cây kế thừa. Tài khoản `op.nv1` đã
> tạo lúc trước là `Administration.Account` thuần, và một object đã tồn tại **không** đổi được
> sang specialization. Lần đăng nhập kế tiếp nhiều khả năng vỡ ở ràng buộc duy nhất.
> Xoá tài khoản cũ trong trang **Accounts** trước khi đăng nhập lại, rồi để JIT tạo lại.

### 0.18 Module chưa có module role: một lần tạo document ra hàng chục lỗi

Module mới **không có module role nào**. Ở mức security Production, mọi document trong đó sinh
lỗi ngay khi có thứ tham chiếu tới — chữ ký là cụm **"(with no roles defined in module X)"**:

```
No access to microflow 'NvmShared.DS_NvmAccount_Current' for user role 'Administrator'
  (with no roles defined in module 'NvmShared').
```

Một entity + hai microflow + một page trong module rỗng cho ra **24 lỗi**, tất cả cùng gốc.
Đừng sửa từng lỗi: tạo module role rồi map vào user role, cả cụm biến mất.

Chi tiết và thứ tự làm (đặc biệt khi agent ghi bằng MCP — MCP **không** tạo được module role):
[mcp-model-work.md](mcp-model-work.md) §"Module role: có schema nhưng KHÔNG có document".

### 0.19 Kiểm gần hết chuỗi SSO mà KHÔNG cần đăng nhập

`/oauth/v2/login` trả `302` kèm nguyên vẹn authorization request. Một lệnh, không cần trình
duyệt, không cần phiên, không cần mật khẩu:

```bash
curl -s -i http://localhost:8080/oauth/v2/login | grep -i "^location"
```

Kết quả đã đo được trên app này:

```
http://localhost:8081/realms/novavolt/protocol/openid-connect/auth
  response_type=code   response_mode=form_post   client_id=nvm-mendix
  redirect_uri=http://localhost:8080/oauth/v2/callback
  scope=openid profile email
  code_challenge_method=S256   code_challenge=...
```

**Chứng minh được, chỉ bằng dòng đó:**

| Điều | Vì sao suy ra được |
|---|---|
| IdP config đang **Active** *và* **Default** | Không có Default thì `/oauth/v2/login` không biết đẩy đi đâu, trả lỗi chứ không 302 |
| Endpoint `authorization_endpoint` import đúng | Nằm ngay trong Location |
| `client_id`, `scope`, `response_mode`, PKCE | Là tham số của chính request |
| `redirect_uri` khớp `redirectUris` của realm | So bằng mắt với realm JSON |

**KHÔNG chứng minh được**: JIT provisioning, attribute mapping (`site_id`), ánh xạ role. Ba
thứ đó chỉ chạy **sau** khi người thật đăng nhập xong.

Hơn hẳn cách ở §0.14 (mở console trên trang Keycloak đọc `location.href`): cách này không cần
trình duyệt hiển thị, nên dùng được cả khi agent chạy không có người ngồi trước máy.

---

## Ledger kế thừa từ CGVibe

> [!warning] Điểm chung của gần hết danh sách này
> Chúng **không** hiện lên ở Error List, **không** hiện ở `mx check`, và chỉ lộ ra ở F5
> hoặc ở runtime thật. Một model "0 errors" không có nghĩa là app chạy.

---

## 1. Khi kiểm tra lỗi

### F4 không kiểm tra gì cả

`F4` = **Synchronize App Directory**. Nó nạp lại file trên đĩa (widget, theme, Java action)
vào model. Nó chạy vài giây rồi im lặng, và người ta tưởng "đã check, không lỗi".

Kiểm nhất quán thật: **View → Error List → Check now**. Chạy app: **F5**.

### `mx check` nói dối khi có tab chưa lưu

`mx check` đọc file `.mpr` **trên đĩa**. Studio Pro giữ tài liệu đang sửa **trong bộ nhớ**
cho tới khi lưu, và `Ctrl+S` chỉ lưu **tab đang active**.

Hậu quả: một tab chưa lưu làm `mx check` báo hàng loạt `CE1613` — *"… no longer exists"* —
về những phần tử **đang tồn tại và hoàn toàn bình thường**. Ở CGVibe chuyện này đã hai lần
khiến người ta đi sửa một mapping vốn không hỏng.

Trước khi chạy `mx check`:
1. **File → Save All**
2. Nhìn tab bar — **chấm tròn trên tab nghĩa là chưa lưu**
3. Chỉ khi không còn chấm nào mới chạy

Khi `mx check` và Error List mâu thuẫn: **tin Error List**, vì nó mô tả model đang sống.

### Exit code của `mx check` là bitmask

`0` = sạch. `1` lỗi · `2` cảnh báo · `4` deprecation · `8` hiệu năng. Cộng dồn. Đừng báo
"exit code khác 0 nên fail" — phải tách bit ra.

### `CW0114` có dương tính giả

Cảnh báo *"accessible through the server API … not used from navigation, a page or a
published service"* **không** đủ để kết luận một microflow đã chết. Ở CGVibe nó báo nhầm cho
ba microflow đang phục vụ traffic thật. Đối chiếu với published service trước khi xoá.

---

## 2. Khi tạo page và microflow mới

### Danh sách allowed roles rỗng — bẫy tốn thời gian nhất

Một page mới có thể ra đời với **allowed roles rỗng**. Rỗng **không phải lỗi model**, nên nó:

- qua được Error List ✓
- qua được `mx check` ✓
- rồi **không role nào mở được page**, chỉ lộ ở F5

Điều tương tự với **microflow dùng làm home page theo role**: allowed roles rỗng thì mọi
kiểm tra đều xanh, rồi hỏng đúng ở role mà nó sinh ra để phục vụ.

**Luôn mở properties của page/microflow mới và nhìn tận mắt danh sách roles.** So với một
page tương đương đã chạy được.

### Entity access biến `setValue` thành no-op im lặng

Một widget ghi vào attribute mà module role chỉ có quyền **Read** sẽ ghi hụt, **không báo lỗi**.
Cùng triệu chứng với DataView để `editability: Never`.

Khi một giá trị "không chịu lưu": kiểm access rule của attribute trước khi nghi ngờ widget.

---

## 3. Khi gọi backend

### Error handling của Call REST nằm ở chuột phải, không phải trong dialog

Double-click activity **Call REST service** → tab **Error handling** chỉ có checkbox
`$latestHttpResponse`. Người ta kết luận "không có tuỳ chọn error handling" và bỏ qua.

Chỗ đúng: **chuột phải lên activity → Set error handling → Custom without rollback**.

Vì sao quan trọng: mặc định là **Rollback**. Với BFF chuyển tiếp, một `400`/`401`/`403` từ
upstream sẽ **rollback cả microflow** và client nhận trang lỗi Mendix thay vì envelope theo
hợp đồng.

### `Custom without rollback` cần một nhánh error đi ra

Sau khi đổi sang Custom without rollback, activity **bắt buộc** có flow error đi ra. Nếu
chưa có, Error List báo hai lỗi *"Sequence flow is not accepted by origin or destination"*.

**Xoá cái flow "bị từ chối" đó sẽ làm Error List sạch và phá huỷ hợp đồng lỗi** — mọi 4xx
upstream biến thành 503 tự chế. Ở CGVibe đây là một defect thật.

Cách đúng: nối nhánh error tới một End event trả `$latestHttpResponse`.

### Mendix từ chối bằng 404, không phải 403

Khi role không được phép gọi một published operation, Mendix trả **404 không có body**, để
không tiết lộ rằng tài nguyên tồn tại. Đừng nhầm với "backend nói không tìm thấy".

Phân biệt bằng **hình dạng envelope**: Mendix trả `{"error":{"code":"404",...}}`, còn backend
của dự án trả envelope riêng có `reasonCode`.

### 401 hết phiên của Mendix khác envelope của backend

Mendix sinh `{"error":{"code":"401","message":"…"}}` **trước khi** microflow chạy. Client nào
phân nhánh theo `reasonCode` trong envelope sẽ bỏ sót nó. **Rẽ theo HTTP status trước.**

---

## 4. Khi làm security

### Published REST liệt kê module role, nhưng runtime phân quyền theo user role

Trường **Allowed roles** của một published REST service hiện **module role**. Runtime lại
phân quyền theo **user role**, và Studio Pro giải quyết ánh xạ đó lúc build.

Hệ quả: một module role mà **không user role nào bao gồm** thì **không có tác dụng gì**,
trong khi editor vẫn hiển thị nó như đã cấu hình. Error List, `mx check`, OpenAPI sinh ra, và
cả probe `401`/`404` — tất cả đều hành xử y như một service đang hoạt động.

Cách kiểm thật: sau khi deploy, đọc `AllowedUserRoles` trong `deployment/model/model.mdp`.
Hoặc đơn giản hơn: **gọi thử bằng session của role mới**. Bảng role không phải bằng chứng.

### Allowed roles là thuộc tính của service, không phải của từng operation

Mở một operation cho role mới nghĩa là mở **toàn bộ service** đó ở ranh giới Mendix. Cổng
thứ hai là allowed roles của chính response microflow.

---

## 5. Khi làm version control

### `Merge Changes Here` merge VÀO branch đang mở

Mở branch **đích** trước, rồi **Version Control → Merge Changes Here**, chọn branch nguồn.
Không đi qua **Manage Branch Lines**.

### Không bao giờ "Revert All Changes"

Đó là thao tác mất dữ liệu. Khi Studio Pro chặn vì working copy dirty:

1. Dừng lại, đừng revert
2. Xem **Changes in Model** và **Changes on Disk** riêng biệt
3. Quyết định thay đổi đó thuộc về việc nào
4. Nếu không rõ chủ sở hữu: **để nguyên copy đó**, tải một copy khác về thư mục mới và làm ở đó

### Merge có thể xoá âm thầm một activity trong microflow

Đã xảy ra ở CGVibe: sau merge, một activity biến mất, gây một chùm `CE0108`/`CE0109` gọi tên
các biến — và lỗi lại hiện ở document của story khác, nên rất khó lần.

Sau mỗi merge có đụng microflow: chạy Check now **và** so lại số activity của những microflow
bị chạm.

**Phục hồi thì đừng dựng lại từ trí nhớ.** Một bản tái dựng "trông hợp lý" có thể âm thầm đổi
tên biến output, tham số, hay cách xử lý lỗi. Lấy lại đúng bản cũ:

1. Tìm storage unit: `grep -rl "<TênDocument>" mprcontents/`
2. Xác nhận mất mát là **cục bộ** chứ không phải thay đổi từ upstream: so file trong working
   copy với chính path đó ở `HEAD` (`git show HEAD:<path>`). File trong working copy nhỏ hơn và
   không còn chứa loại activity mong đợi = bị xoá cục bộ.
3. Đọc chuỗi in được trong blob đã commit để lấy lại nguyên văn: loại activity, operation hoặc
   microflow được gọi, tên biến output, từng parameter mapping và biểu thức, và cả thiết lập
   error handling.
4. Thêm lại element và các sequence flow **qua Studio Pro**, rồi chạy lại Check now.

Cả bốn bước đều chỉ **đọc** Git. **Không bao giờ checkout riêng lẻ một file `.mxunit`** — index
trong `.mpr` và các unit file phải khớp nhau, checkout từng phần làm chúng lệch.

### `mx git-merge` phân loại conflict mà không cần mở app

Khi `.mpr` conflict, `mx git-merge BASE MINE THEIRS <sha> <sha> <sha>` trả về phán quyết theo
từng unit — `AutoResolved` hay `ManualResolution` — nên biết được **phần nào cần người quyết**
trước khi ai đó phải ngồi vào Interactive Merge.

### Dấu tích trong Interactive Merge không đảm bảo element đã vào bản merge

Đã xảy ra ở CGVibe trên 11.12: hai bên cùng chỉ *thêm* operation vào một Published REST service,
Interactive Merge hiện xanh, Studio Pro báo merge xong, consistency checker im lặng — mà
operation của một bên biến mất. Sau mỗi merge đụng document dùng chung, **so lại danh sách phần
tử với cả hai nhánh cha**, đừng tin báo cáo merge.

### "Feature branch sạch" không có nghĩa là "đã merge"

Ba câu hỏi khác nhau, phải trả lời riêng: **đã commit chưa · đã push chưa · branch tích hợp
đã chứa nó chưa**.

---

## 6. Khi dùng widget và thư viện

### `.mpk` mới copy vào là vô hình cho tới khi F4

Chép file `.mpk` vào `widgets/` **không** đăng ký nó với model đang mở. Nhấn **F4**
(Synchronize App Directory) thì nó mới xuất hiện.

### Nút Sign-out trong Toolbox có thể không phải logout của bạn

Nó thực hiện sign-out của nền tảng **trực tiếp**, nên microflow cầu nối để thu hồi session ở
backend **không bao giờ chạy**. Ở CGVibe, refresh token phía Identity vẫn sống đủ 7 ngày sau
một lần "đăng xuất thành công".

Dùng Button thường trỏ tới microflow logout của mình.

### Điều khiển ở phạm vi session thuộc về layout, không thuộc page

Nút logout đặt trên một page chỉ phủ đúng page đó. Mọi page thêm sau này sẽ không có chỗ
đăng xuất. Đặt ở **layout** dùng chung.

---

## 7. Bảng triệu chứng → nghi phạm đầu tiên

| Triệu chứng | Nghi phạm | Kiểm gì trước |
|---|---|---|
| Nhiều app trùng tên trong launcher | Nhiều thư mục local khác nhau | Liệt kê đường dẫn `.mpr` tuyệt đối |
| `mx check` báo `CE1613` hàng loạt | Tab chưa lưu | Nhìn chấm trên tab → Save All → chạy lại |
| Không chuyển/merge branch được | Working copy dirty thật | Xem cả hai tab Changes, xác định chủ sở hữu |
| Mọi check xanh nhưng page 404 ở F5 | Allowed roles rỗng | Mở properties của page, nhìn danh sách roles |
| Giá trị không chịu lưu | Entity access chỉ Read | Kiểm access rule của attribute |
| Mọi lỗi upstream thành 503 | Call REST đang ở Rollback | Chuột phải activity → Set error handling |
| Gọi API trả 404 rỗng | Mendix từ chối theo role | So hình dạng envelope; gọi lại bằng role được phép |
| Widget mới không xuất hiện | `.mpk` chưa đồng bộ | F4 |
| Hàng chục lỗi "No access ... for user role" cùng lúc | Module chưa có module role | Đọc cụm "(with no roles defined in module X)" — §0.18 |
| Menu có trong model nhưng không hiện | User role thiếu module role | `mx export-security-overview`, xem `documentAccess` rỗng — §0.12 |
| Đăng nhập SSO xong mà role vẫn là mặc định | Claim role không phẳng, hoặc tên không trùng | Giải mã access token, tìm claim mảng chuỗi cấp cao nhất — §0.16 |

---

## Nguồn chính thống

- https://docs.mendix.com/refguide/consistency-errors/
- https://docs.mendix.com/refguide/mx-command-line-tool/app/
- https://docs.mendix.com/refguide/pushing-pulling/
- https://docs.mendix.com/refguide/resolving-conflicts/
- https://docs.mendix.com/refguide/troubleshoot-version-control-issues/
