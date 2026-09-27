# ADR-047 — Passport là snapshot đã ký, lọc theo trường, mặc định từ chối

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-26 |
| **Liên quan** | `docs/scope.md` §6.1, §7.7, §9/M12 · ADR-007, ADR-017, ADR-045 |

---

## Context

Từ 18/02/2027 pin EV đưa ra thị trường EU cần battery passport truy cập qua QR (Regulation (EU) 2023/1542; mốc ngày
lấy từ scope, chưa đối chiếu văn bản gốc trong phiên này). DoD M12: QR theo GS1 Digital Link trả đúng passport; năm bên
đọc (public, consumer, recycler, repairer, regulator) thấy năm tập trường khác nhau; public không bao giờ thấy lot nhà
cung cấp, recipe, genealogy nội bộ; passport đã công bố không sửa được và công bố lại là version mới trỏ về version cũ;
mọi lượt đọc của regulator có audit; sản phẩm B (không xuất EU) không có passport nhưng có carbon footprint. Lab: thêm
trường mà quên gắn `AccessClass` thì phải bị từ chối.

## Decision

- **FB Passport** tách `BatteryModel` (GTIN-14, dùng chung) khỏi pack (serial). Model mang cờ `RequiresPassport`;
  carbon footprint theo (sản phẩm, nhà máy, năm), có version, ghi cho mọi sản phẩm.
- **Passport là snapshot**, không phải view trên bảng gốc. `PreparePassport` dựng nội dung từ model, carbon record của
  năm sản xuất (đọc từ serial) và bằng chứng của pack: trạng thái unit (traceability, cùng transaction) và tổ tiên trong
  genealogy (closure), recipe của cuộn điện cực (read model trace). Nội dung là danh sách trường + hash SHA-256 trên dạng
  chuẩn hoá.
- `PublishPassport` cần chữ ký `ComplianceOwner` trên đúng hash, không phải người dựng bản nháp (dùng
  `ISignatureVerifier` của Quality). Công bố lại = bản nháp mới với `PreviousVersion`.
- **Bất biến ở DB**: trigger `passport.TR_Passports_Immutable` chặn đổi nội dung/hash/GTIN/version trước, chặn mọi
  UPDATE/DELETE trên bản đã Published, kể cả với tài khoản có quyền; nvm_app chỉ được UPDATE các cột công bố.
- **Phân quyền theo trường**: mỗi trường thuộc một nhóm trong `PassportFieldCatalog`; ma trận nhóm × bên đọc lấy đúng
  scope §7.7. Trường không có trong catalog, nhóm lạ, bên đọc lạ → không thấy (fail closed). Nhóm Health với consumer chỉ
  mở khi token có `dpp_owner_of` chứa đúng serial.
- **Resolver** `GET /01/{gtin}/21/{serial}` công khai; không token là public. Site lấy từ serial. GTIN không khớp model →
  404; GTIN sai chữ số kiểm → 400; token mang nhiều `dpp_audience` → 403 (không đoán quyền cao hơn).
- **Audit regulator** ghi trong cùng kết nối trước khi trả dữ liệu; ghi lỗi thì không trả.
- **Keycloak**: 5 client scope `dpp-*` gắn claim `dpp_audience` và audience `nvm-api`. Public/consumer dùng client
  `nvm-dpp` (consumer là scope tuỳ chọn, ownership qua thuộc tính người dùng). Recycler, repairer, regulator là tổ
  chức: mỗi bên một client confidential riêng với scope mặc định cố định, không xin được scope của bên khác.

## Consequences

**Được**

- Không có đường từ QR vào bảng genealogy: passport chỉ đọc snapshot.
- Quên gắn nhóm cho trường mới làm trường biến mất, không làm lộ dữ liệu.
- Version cũ vẫn đọc được nguyên vẹn (`?version=1`), phục vụ hồ sơ dài hạn.

**Mất / phải chịu**

- Consumer tự xin được scope `dpp-consumer` trên client public; điều nó mở thêm (Health) còn cần `dpp_owner_of`, do
  admin gán. Chưa có luồng chuyển chủ sở hữu.
- Dữ liệu vận hành (SoH, chu kỳ) lúc xuất xưởng là 100 %/0; cập nhật sau bán hàng cần nguồn dữ liệu ngoài MES, chưa có.
- Năm sản xuất suy từ chữ số năm của serial (ADR-007): đúng trong 10 năm; passport lâu hơn phải dựa vào version đã ký,
  không dựng lại từ serial.
- Genealogy lấy từ read model (eventual consistency): passport dựng trước khi projection bắt kịp sẽ thiếu nút. Người
  duyệt ký trên nội dung nhìn thấy; dựng lại là version mới.
- Mendix DPP Viewer chưa làm.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| RBAC theo endpoint (mỗi audience một endpoint) | Scope đòi field-level; endpoint riêng vẫn phải lọc trường và dễ trả thừa |
| Mặc định cho phép, liệt kê trường cấm | Trường mới quên cấm sẽ lộ; lab M12 đòi fail closed |
| View SQL trên bảng gốc cho public | Lộ lot/recipe khi view sai; không có version bất biến để ký |
| Audience là optional scope của một client công khai | Ai cũng xin được `dpp-regulator` |

## Evidence

- `PassportAccessTests` (unit): 5 test riêng cho 5 bên đọc, 5 tập trường khác nhau; 4 bên ngoài regulator không thấy
  lot/recipe/genealogy (kể cả trong giá trị); trường không có nhóm bị từ chối với cả 5 bên (lab); bên đọc lạ thấy 0
  trường; hash không phụ thuộc thứ tự trường.
- `PassportTests` (SQL Server + PostgreSQL Testcontainers): passport dựng từ genealogy thật (cell → module → pack, cuộn
  `ROL-NV1-260825-CT1-004`, recipe `RCP-COAT v2`); thiếu carbon → `MISSING_EVIDENCE`; người dựng tự ký →
  `SEPARATION_OF_DUTIES`; công bố hai lần → `PASSPORT_ALREADY_PUBLISHED`; UPDATE thẳng nội dung → SqlException 51000;
  version 2 trỏ về 1, version 1 vẫn đọc được; 2 lượt đọc regulator → 2 dòng audit, public không ghi; sản phẩm B →
  `PASSPORT_NOT_REQUIRED` nhưng carbon record đọc được.
- `PassportResolverHttpTests` (app thật qua HTTP, JWT ký thật): URL GS1 không token → tập public; regulator → thấy
  genealogy và audit +1; consumer chủ sở hữu thấy SoH, người khác không; hai audience → 403; GTIN sai chữ số kiểm → 400;
  serial lạ → 404.
- Keycloak realm chưa được nạp lại trên runtime trong phiên này: cấu hình client scope là **chưa kiểm trên Keycloak thật**.
