# Mendix nói chuyện với backend .NET

Ba kênh, ba mục đích. Đúng mô hình mà [Opcenter Execution Foundation Starter
Kit](https://marketplace.mendix.com/link/component/208792/Siemens/Opcenter-Execution-Foundation-Starter-Kit)
dạy: **authentication → data query → logic execution**.

Ràng buộc **K10** (`AGENTS.md`): Mendix **chỉ** đi qua Public Object Model và Command API.
Không connection string tới database của .NET.

---

## 1. Bản đồ ba kênh

| Kênh | Backend | Mendix dùng | Đọc/Ghi |
|---|---|---|---|
| Authentication | Keycloak, port 8081 | Module **OIDC SSO** (Marketplace) | — |
| Data query | **Public Object Model**, OData v4 `/pom/v1` | **Consumed OData Service** | Chỉ đọc |
| Logic execution | **Command API**, REST `/api/v1/commands/*` | **Call REST service** trong microflow | Chỉ ghi |

Đây chính là CQRS, chỉ khác tên. Public Object Model = read side. Command = write side.

---

## 2. Authentication — OIDC với Keycloak

**Module cần cài**: `OIDC SSO` từ Marketplace, kèm dependency **Community Commons**.
Bản OIDC ≤ 4.3.0 cần thêm **Encryption** — kiểm version lúc cài, đừng cài thừa.

**Cấu hình trỏ tới Keycloak**:

| Trường | Giá trị |
|---|---|
| Discovery URL | `http://localhost:8081/realms/novavolt/.well-known/openid-configuration` |
| Client ID | `nvm-mendix` |
| Realm | `novavolt` |

**Port**: Keycloak ở **8081**, không phải 8080. Mendix Studio Pro giữ 8080 cho app chạy local.
Quyết định này chốt ở C05, ghi trong `.env.example`.

**Role mapping**: realm role của Keycloak (`Operator`, `LineLeader`, `QaEngineer`,
`QaManager`, `ProductionManager`, `ComplianceOwner`) ánh xạ sang **module role** trong Mendix.

**`site_id`**: là user attribute trong Keycloak, đưa vào token qua protocol mapper. Mendix
dùng nó cho XPath constraint — xem §5.

---

## 3. Data query — Consumed OData Service

**Tạo**: Right-click module → **Add other** → **Consumed OData Service**, dán URL metadata:

```
http://localhost:5080/pom/v1/$metadata
```

Studio Pro sinh ra entity ngoài (external entity) tương ứng. Dùng chúng trong DataView,
Data Grid, List View như entity thường — trừ việc **không ghi được**.

**Quy ước của Public Object Model** (backend đã ép sẵn, biết để khỏi ngạc nhiên):

| Quy ước | Nghĩa với Mendix |
|---|---|
| `SiteId` bị ép ở server theo token | Không cần tự lọc, và **không tin được** nếu tự lọc |
| `$top` mặc định 50, tối đa 1.000 | Grid kéo nhiều hơn sẽ bị cắt |
| Mọi collection có `@odata.nextLink` | Paging hoạt động bình thường |
| `ETag` trên entity đơn | Dùng được cho optimistic concurrency phía UI |

> [!warning] Đổi read model là đổi hợp đồng
> Backend đổi shape của POM sẽ làm external entity trong Mendix lệch. Có snapshot của
> `$metadata` trong contract test ở phía .NET (C12) đúng để bắt việc này ở CI, nhưng Mendix
> phải **refresh** consumed service thì mới thấy. Sau mỗi lần backend đổi POM: right-click
> consumed service → **Refresh**.

---

## 4. Logic execution — Call REST service

Mọi thao tác ghi đi qua `POST /api/v1/commands/<context>/<command>`.

**Thân request** luôn có dạng:

```json
{
  "idempotencyKey": "<Mendix sinh, giữ nguyên khi retry>",
  "siteId": "NV1",
  "occurredAt": "2026-08-25T03:15:40+00:00",
  "payload": { }
}
```

`idempotencyKey` **phải giữ nguyên khi người dùng bấm lại**. Bus là at-least-once, và handler
phía .NET dùng key này để không xử lý hai lần. Sinh key mới mỗi lần bấm là phá cơ chế đó.

**Thân response** luôn có dạng thống nhất:

```json
{
  "accepted": false,
  "reasonCode": "MATERIAL_EXPOSURE_EXCEEDED",
  "reasonText": "Lot ELY-SUP-240612-A778 đã mở 4h32m, giới hạn 4h00m.",
  "blockingRules": [ … ],
  "allowedNextActions": ["RequestOverride", "SelectAnotherLot"],
  "correlationId": "OPRUN-8891"
}
```

**Hiển thị `reasonText` nguyên văn.** Đừng hard-code chuỗi tiếng Việt cho từng `reasonCode`
trong Mendix — backend đã trả sẵn câu hoàn chỉnh, và khi thêm rule mới thì UI không phải sửa.
`allowedNextActions` quyết định nút nào hiện.

**Error handling**: đặt **Custom without rollback** (chuột phải activity → Set error handling).
Mặc định `Rollback` sẽ nuốt mọi 4xx thành lỗi Mendix. Xem `studio-pro-traps.md` §3.

---

## 5. Multi-site — `SiteId` là ràng buộc bảo mật

Dự án có hai site: `NV1` (Việt Nam) và `DE1` (Đức). Rò rỉ dữ liệu giữa hai site là **lỗi bảo
mật**, không phải lỗi hiển thị.

**Phía Mendix**: XPath constraint trên entity access

```
[SiteId = '[%CurrentUser%]/Site/Code']
```

**Phía backend**: server ép filter theo token, không tin client.

**Hai lớp là cố ý.** Nếu chỉ dựa vào Mendix thì một lời gọi REST trực tiếp sẽ vượt qua. Nếu
chỉ dựa vào backend thì grid vẫn kéo về rồi mới bị chặn — chậm và lộ số lượng bản ghi.

Test bắt buộc ở M10: đăng nhập bằng user thuộc NV1, gọi **mọi** endpoint, và không được thấy
một dòng dữ liệu DE1 nào.

---

## 6. Bốn app Mendix của dự án

| App | Persona | Màn hình | Kỹ thuật đặc thù |
|---|---|---|---|
| `NvmShared` | — | (module dùng chung) | OIDC config, connector POM, response mapper |
| `NvmShopFloor` | Operator, Supervisor | Dispatch list, Scan station, Data collection, WIP board, Andon | Nanoflow gọi REST, refresh định kỳ |
| `NvmQuality` | QA Engineer, QA Manager | NCR inbox, Investigation, Disposition, E-signature, SPC | **Mendix Workflow engine**, parallel approval |
| `NvmTrace` | Supervisor, Compliance | Trace Explorer, Recall impact, DPP Viewer | Consumed OData, widget vẽ đồ thị |

**Bắt đầu từ `NvmShared`.** Viết connector và response mapper một lần, ba app kia import.
Đây đúng cách Siemens tổ chức Extension App, và tránh copy-paste microflow ba lần.

---

## 7. Màn hình Scan Station — mẫu cho mọi màn hình operator

Luồng chuẩn, dùng lại cho mọi trạm:

1. Operator quét serial
2. Mendix gọi **POM** đọc trạng thái unit + rule đang chặn
3. Nếu bị chặn → hiện `reasonText` + `allowedNextActions`, **dừng**
4. Nếu hợp lệ → hiện EWI + form nhập liệu
5. Operator nhập, bấm Complete
6. Mendix gọi **Command API** kèm `idempotencyKey`
7. `accepted: true` → xanh, sẵn sàng quét cái tiếp theo

**Điểm quan trọng**: bước 2 và bước 6 dùng **hai kênh khác nhau**. Đọc bằng OData, ghi bằng
REST. Đừng gộp.

---

## 8. Khi backend chưa chạy

Mendix phải hiện *"hệ thống tạm thời không phản hồi"*, **không** phải trang lỗi trắng.

Đây là hệ quả của N15 (MES chết thì dây chuyền vẫn chạy): người đứng máy phải biết mình đang
ở tình trạng nào, chứ không phải nhìn một màn hình vô nghĩa.

Kiểm bằng cách tắt `Nvm.App.Execution` rồi thao tác trên Mendix.
