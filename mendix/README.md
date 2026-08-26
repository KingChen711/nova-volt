# Mendix — `NvmShopFloor`

App Mendix **không** nằm trong repo này. Nó sống trong Team Server repo của chính nó.
Thư mục này chỉ giữ tài liệu để người khác (và chính bạn sau ba tháng) dựng lại được.

| | |
|---|---|
| Tên app | `NvmShopFloor` |
| Team Server (Git) | `https://git.api.mendix.com/0b71f9c2-da6d-44af-b37c-90da46cf3253.git/` |
| Studio Pro | **11.12.3** — không phải 11.12.1 như `docs/plans/M0-bootstrap.md` ghi |
| Version control | **Git**, không phải SVN |
| Branch chính | `main` |
| Thư mục làm việc trên máy hiện tại | `C:\Users\Kingc\Mendix\NvmShopFloor-main` |
| Port khi `Run Locally` | **8080** (Keycloak nhường sang 8081 — quyết định ở C05) |

> [!warning] `mx.exe` phải đúng version
> Máy này có cả `D:\apps\Mendix\11.12.1` và `D:\apps\Mendix\11.12.3`. Chạy `mx check` bằng
> bản 11.12.1 trên app đã sửa bằng 11.12.3 sẽ báo lỗi giả. Dùng
> `D:\apps\Mendix\11.12.3\modeler\mx.exe`.

---

## Module Marketplace đã cài

Đọc bằng `mx dump-mpr --unit-type='Projects$Module'` — đây là số thật trong `.mpr`, không
phải số nhớ được.

| Module | Version | Vì sao có mặt |
|---|---|---|
| `OIDC` (OIDC SSO) | **4.7.0** | Đăng nhập qua Keycloak |
| `CommunityCommons` | 11.5.1 | Dependency bắt buộc của OIDC |
| `UserCommons` | 2.4.0 | Dependency bắt buộc của OIDC |
| `Administration` | 4.5.0 | Đi kèm Blank Web App; cho entity `Account` |
| `Atlas_Core` | 4.3.7 | Template |
| `Atlas_Web_Content` | 4.3.0 | Template |
| `DataWidgets` | 3.11.2 | Template |
| `NanoflowCommons` | 7.2.1 | Template |
| `WebActions` | 2.11.2 | Template |
| `FeedbackModule` | 5.0.0 | Template |

**Không** cần `Encryption` (chỉ cho OIDC ≤ 4.3.0) và **không** cần `Events` — tài liệu
Mendix liệt kê `Events` là dependency nhưng trên Mendix 11 + OIDC 4.7.0 thì app 0 lỗi mà
không có nó.

Cài dependency **trước** OIDC SSO. Ngược lại thì Error List đỏ hàng loạt vì thiếu tham chiếu.

---

## Module tự viết

| Module | Nội dung |
|---|---|
| `MyFirstModule` | Giữ nguyên từ template. Chứa trang `Home_Web` và module role `Anonymous` (rỗng quyền) |
| `NvmShared` | Phần dùng chung của NovaVolt |

### `NvmShared`

| Document | Loại | Việc |
|---|---|---|
| `NvmAccount` | Entity, specialization của `Administration.Account` | Thêm `SiteId` — chỗ chứa claim `site_id` |
| `NvmAccount/DS_NvmAccount_Current` | Microflow | Data source cho `Home_Web`: lấy `NvmAccount` của người đang đăng nhập |
| `Sso/CustomATP_KeycloakRealmRoles` | Microflow | Ánh xạ realm role của Keycloak sang user role Mendix |

---

## Đường đi từ Keycloak vào app

Ba mảnh rời nhau, hỏng mảnh nào cũng cho triệu chứng khác nhau:

**1. Đăng nhập** — module OIDC, cấu hình *runtime* (xem mục dưới).

**2. `site_id`** — mapper `site-id` trong `deploy/keycloak/realm-novavolt.json` đưa claim vào
token; JIT provisioning của OIDC ghi nó vào `NvmShared.NvmAccount.SiteId` qua **Attribute
Mapping** ở bước *Creating Users*.

**3. Role** — mapper `mendix-roles` sinh claim phẳng `mendix_roles: ["Operator"]`;
microflow `CustomATP_KeycloakRealmRoles` đọc claim đó và cấp user role Mendix **trùng tên**.

> Không dùng được `realm_access.roles` của Keycloak: nó là mảng **lồng trong object**, còn
> module OIDC chỉ đọc được mảng chuỗi ở cấp cao nhất của payload. Chi tiết ở
> [`deploy/keycloak/README.md`](../deploy/keycloak/README.md).

---

## Cấu hình IdP là DỮ LIỆU, không phải model

Đây là điểm dễ mất công nhất. Cấu hình OIDC nằm trong **database của app đang chạy**, không
nằm trong `.mpr` và **không** đi theo Git. Xoá database local là mất sạch, phải làm lại tay.

### Đăng nhập để cấu hình

Trang `login.html` **không** có bảng chọn demo user — Mendix 11 không dựng cái đó. Chỉ có
`MxAdmin` hoặc một demo user gõ tay. Mật khẩu demo nằm nguyên văn trong model:

```bash
mx dump-mpr --unit-type='Security$ProjectSecurity' --output-file sec.json <app.mpr>
# đọc bằng encoding='utf-8-sig', xem khoá demoUsers[]
```

Dùng `demo_administrator` — nó có role Administrator nên thấy menu **IdP configuration**.

### Cấu hình đang chạy (alias `keycloak-novavolt`)

**Edit** một cấu hình có sẵn mở hộp *Configure a SSO connection* hỏi Alias trước, **Next** mới
vào wizard 4 bước.

| Bước | Giá trị |
|---|---|
| 1. Endpoints | `http://localhost:8081/realms/novavolt/.well-known/openid-configuration` → **Import IdP-metadata** |
| 2. General Configuration | Client ID `nvm-mendix` · secret `dev-only-not-a-secret` · response mode **Form post** · scopes `openid profile email` · PKCE S256 |
| 3. Creating Users | Custom user entity `NvmShared.NvmAccount` · principal attribute `Name` · metering identifier `email` · default user role `User` · **4 dòng mapping bên dưới** |
| 4. Assigning Roles | Token **ACCESS-TOKEN** · Custom AccessToken processing microflow `NvmShared.CustomATP_KeycloakRealmRoles` |

Attribute Mapping ở bước 3 — **cả bốn dòng đều bắt buộc**:

| IdP Attribute | Configured Entity Attribute |
|---|---|
| `sub` | `Name` |
| `name` | `FullName` |
| `email` | `Email` |
| `site_id` | `SiteId` |

> [!warning] Tài liệu Mendix sai chỗ này
> `docs.mendix.com/appstore/modules/oidc/` viết *"You cannot use the IdP claim which is the
> primary attribute identifying the user"*. Thực tế OIDC 4.7.0 **bắt buộc** map `sub` → `Name`.
> Thiếu nó thì bấm Next ở bước 3 ra hai hộp lỗi nối nhau, và chỉ hộp thứ hai nói đúng việc:
>
> ```
> Invalid userprovisioning configuration
> Map Principal attribute to any one of attribute of userclaims
> ```

`site_id` **không** có trong `claims_supported` của Keycloak nên phải tạo tay: trong hộp
*Add Claim Map* bấm **Search** → **New** → điền Claim Name và Friendly Name `site_id` → **Save**
→ cuộn xuống cuối danh sách chọn dòng đó → **Select**.

Sau **Save and Finish**, ở danh sách phải có **Active = Yes** *và* **Default = Yes**. Thiếu
`Default` thì `/oauth/v2/login` không biết đẩy người dùng đi đâu — im lặng, không báo lỗi.
(Edit rồi lưu lại **không** làm mất hai cờ này — đã kiểm.)

> [!warning] Mở Edit ra, combobox trông như đã chọn nhưng thực ra RỖNG
> Giá trị cũ được hiện dưới dạng *placeholder*. Ô đã thật sự có giá trị mới có nút **×** để xoá.
> Kiểm chắc chắn bằng console: phần tử có class `widget-combobox-placeholder-empty` là **rỗng**.

### Kiểm không cần đăng nhập

```bash
curl -s -i http://localhost:8080/oauth/v2/login | grep -i "^location"
```

Location phải chứa `client_id=nvm-mendix`, `response_mode=form_post`,
`redirect_uri=http://localhost:8080/oauth/v2/callback`, `code_challenge_method=S256`. Ra được
302 nghĩa là cấu hình đang Active **và** Default.

---

## Chạy local

Điều kiện: `make up` ở repo chính đã xanh (Keycloak ở `localhost:8081`).

1. Mở `C:\Users\Kingc\Mendix\NvmShopFloor-main\NvmShopFloor.mpr` bằng Studio Pro 11.12.3.
2. **File → Save All**.
3. **View → Error List → Check now** — phải **0 lỗi** trước khi chạy.
4. **F5** (Run Locally). **F4 là *Synchronize App Directory*, không phải kiểm tra lỗi.**
5. Mở **`http://localhost:8080/oauth/v2/login`** → chuyển sang trang đăng nhập Keycloak.
6. Đăng nhập `op.nv1` / `dev`.

> [!warning] Vào thẳng `http://localhost:8080` thì KHÔNG tự chuyển hướng
> Đã đo: `GET /` trả **200**, không phải 302. App đang bật *Allow anonymous users* và
> *Sign-in page* để trống (đúng khuyến nghị của tài liệu OIDC), nên khách vãng lai render
> `Home_Web` ở trạng thái anonymous — DataView rỗng, trông như app hỏng.
>
> Quyết định còn treo, chọn một trong hai khi cần đường vào tự nhiên:
>
> - **Tắt anonymous**, thay `theme/web/login.html` bằng `login-automatic.txt` của module OIDC.
>   Đây là cách chuẩn trong tài liệu, và gỡ luôn được dây chuyền `CE0157`/`CE2729` mà anonymous
>   kéo theo.
> - **Giữ anonymous**, đặt *Role-based home page* cho `Anonymous` trỏ tới một trang tự đẩy sang
>   `/oauth/v2/login`.

Kiểm tra headless, không cần mở Studio Pro:

```bash
"D:/apps/Mendix/11.12.3/modeler/mx.exe" check "C:/Users/Kingc/Mendix/NvmShopFloor-main/NvmShopFloor.mpr"
```

---

## Không commit `.mpr` khi đang dirty

`.mpr` là file nhị phân — Git **không merge được** nó. Hai người sửa cùng lúc là conflict
phải giải bằng Studio Pro, không phải bằng editor.

Ba luật, không có ngoại lệ:

1. **File → Save All trước mọi thao tác version control.** Tab còn chấm tròn = chưa lưu.
   `mx check` đọc file trên đĩa, còn Studio Pro giữ tài liệu đang sửa trong bộ nhớ — một tab
   chưa lưu làm `mx check` báo `CE1613` "no longer exists" về những thứ **đang tồn tại**.
2. **Commit từ trong Studio Pro** (Version Control → Commit), không `git commit` từ terminal.
3. **Không bao giờ** dùng *Revert All Changes* để thoát trạng thái dirty. Đó là thao tác mất
   dữ liệu.

Sửa model bằng Studio Pro MCP cũng vậy: MCP đổi model **sống** trong bộ nhớ, `.mpr` trên đĩa
chưa đổi cho tới khi **Save All**.

---

## Trạng thái D3

Đã chạy đầu-cuối ngày 2026-08-26: mở app → chuyển sang Keycloak → đăng nhập `op.nv1` → trang chủ
hiện `Operator NV1` · `op.nv1@novavolt.example` · `Site: NV1` · role `User` + `Operator`.

`Operator` chứng minh microflow ATP chạy: default user role chỉ cấp `User`, nên `Operator` chỉ
có thể đến từ claim `mendix_roles`. `Site: NV1` chứng minh attribute mapping chạy.

## Còn thiếu ở M0

- Ảnh chứng minh D3: `docs/evidence/M0-D3-mendix-login.png`.
- Commit công việc model lên Team Server (làm từ Studio Pro, không phải `git` ở terminal).
- `AGENTS.md` phạm vi app (ràng buộc K10/K11 cho Maia trong Studio Pro) — cân nhắc ở M4.
