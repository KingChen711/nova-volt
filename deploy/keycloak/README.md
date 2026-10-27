# Keycloak realm — `novavolt`

Chú thích cho `realm-novavolt.json` nằm ở đây, **không** nằm trong file JSON.

> [!danger] Không được chú thích trong realm JSON
> Keycloak đọc file bằng Jackson ở chế độ strict: một trường lạ (vd `"_comment"`) làm import chết hẳn. Container
> khởi động lại liên tục và healthcheck báo unhealthy, không có gợi ý nào chỉ về dòng chú thích.

## Realm được nạp thế nào

`docker-compose.yml` mount thư mục này vào `/opt/keycloak/data/import` (chỉ đọc) và chạy `start-dev --import-realm`.
Không có volume cho H2: mỗi container mới import lại realm từ file, nên file này là nguồn sự thật duy nhất; sửa tay
trong Admin Console mất khi tạo lại container.

> [!warning] `docker compose restart` KHÔNG nạp lại realm
> H2 nằm trong lớp ghi của container: `restart` giữ database cũ và Keycloak bỏ qua import vì realm đã có. Sau khi sửa
> file, tạo lại container rồi đặt lại mật khẩu user thử:
>
> ```bash
> docker compose up -d --force-recreate --wait keycloak
> make keycloak-users
> ```

## Nội dung

**Token** sống 600 giây (`accessTokenLifespan`), như hệ thật; hết hạn thì MOM trả 401 mã `6401` (lab L05).

**Realm role** chia theo khu của web, như hệ thật chia app theo nhóm người dùng:

| Role | Khu |
|---|---|
| `mom-admin` | Quản trị; thấy mọi khu |
| `line-operator` | Xưởng |
| `line-lead` | Xưởng, gọi hàng; giữ hàng, hủy lệnh đóng băng |
| `quality` | NC hàng loạt, kết luận NC, containment, tháo rã |
| `yard` | Cổng và dock |
| `shipping` | Thành phẩm |
| `logistics` | Gọi hàng |

**Client**

| Client | Dùng cho | Flow |
|---|---|---|
| `nvm-web` | Web Next.js ở `http://localhost:3100` | Authorization code + PKCE (S256), public |
| `nvm-cli` | Script OData chỉ đọc, suite | Direct access grant (tên đăng nhập + mật khẩu), public |

**User thử**: `admin.nv1`, `op.nv1`, `lead.nv1`, `qa.nv1`, `yard.nv1`, `ship.nv1`, `log.nv1`. Mật khẩu **không** nằm
trong realm JSON (K13): `make keycloak-users` đặt cùng một mật khẩu `NVM_DEMO_USER_PASSWORD` từ `.env` cho mọi user.

Role gán cho một transition của state machine phải trùng tên một role ở đây; gõ sai thì không ai đi được transition đó
(lab L06).
