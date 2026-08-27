---
name: mendix-manual
description: >-
  Hướng dẫn người dùng TỰ TAY thao tác Mendix Studio Pro cho dự án NovaVolt MES:
  tạo app, domain model, page, microflow, workflow, security, OIDC, consume OData/REST,
  Team Server, mx check, chạy local. Dùng skill này bất cứ khi nào công việc chạm tới
  Mendix — kể cả khi người dùng chỉ nói "làm màn hình operator", "gọi API từ Mendix",
  "app không chạy", "commit lên Team Server", mà không nhắc tên Mendix.
  Studio Pro MCP: tầng đọc luôn được dùng; tầng ghi CHỈ khi người dùng yêu cầu rõ
  trong lượt hiện tại. Mặc định là người dùng tự bấm, agent viết các bước.
---

# Mendix — chế độ người dùng tự thao tác

## 0. Điều khác biệt lớn nhất của dự án này

Ở dự án CGVibe trước đây, agent làm phần lớn công việc Mendix qua Studio Pro MCP.
**Dự án này thì không.** Người dùng muốn **tự tay thao tác** để học Mendix bằng cơ bắp,
không phải bằng cách đọc lại diff của agent.

Hệ quả bắt buộc:

| Việc | Ai làm |
|---|---|
| Mọi thao tác trong Studio Pro | **Người dùng** |
| Quyết định thiết kế, đặt tên, chọn pattern | Agent đề xuất → người dùng chốt |
| Viết ra các bước bấm | Agent |
| Giải thích vì sao | Agent |
| Đọc log, chẩn đoán lỗi, đối chiếu tài liệu | Agent |
| Chạy `mx check`, đọc `deployment/model`, grep file | Agent (chỉ đọc) |

Agent vẫn được **đọc** file trong thư mục app (`.mpr` là nhị phân, nhưng
`deployment/`, `javasource/`, `theme/`, `widgets/` đọc được) để chẩn đoán.

### 0.1 Studio Pro MCP — ba tầng, không phải bật/tắt

| Tầng | Ví dụ tool | Được dùng khi nào |
|---|---|---|
| **Đọc** | `ped_read_document`, `ped_check_errors`, `list_modules`, `pg_read_page`, `read_file` | **Luôn được.** Không cần hỏi |
| **Ghi** | `ped_create_document`, `ped_update_document`, `pg_patch_page`, `ped_create_module`, `install_marketplace_module`, `write_file` | **Chỉ khi người dùng nói rõ trong lượt hiện tại** |
| **Mặc định** | — | Người dùng tự bấm, agent viết các bước |

**Tầng đọc luôn mở** vì nó không lấy đi cơ hội học của ai, mà lại làm hướng dẫn chính xác hơn.
Thay vì hỏi *"Security level đang là gì?"* rồi chờ, agent tự đọc model và biết ngay. Ít vòng
hỏi-đáp hơn, ít đoán sai hơn.

**Tầng ghi cần cho phép từng lần.** Cùng nguyên tắc với `AGENTS.md` §1.1 về commit: người dùng
nói *"làm hộ tôi phần này"* thì làm **đúng phần đó**, không suy rộng sang lượt sau. Mặc định
vẫn quay về người dùng tự bấm.

**Sau khi ghi bằng MCP, luôn nói rõ đã tạo/sửa document nào và vì sao** — người dùng vẫn phải
hiểu model của mình, chỉ là không phải tự click.

> **Trước khi ghi bất cứ thứ gì bằng MCP, đọc [mcp-model-work.md](references/mcp-model-work.md).**
> Nó chứa quy trình bắt buộc, hệ thống schema, error protocol và các giới hạn đã biết. Ghi model
> mà không đọc file đó là cách nhanh nhất để tạo lỗi dây chuyền trong app của người dùng.

### 0.2 MCP kiểm chứng được gì, và KHÔNG kiểm chứng được gì

| Kiểm chứng được | Không kiểm chứng được |
|---|---|
| Nội dung model: entity, microflow, page, security, module role | **Tên menu, nút, nhãn hộp thoại trong Studio Pro** |
| Danh sách module đã cài và version | Vị trí một mục trong UI |
| Lỗi model (`ped_check_errors`) | Thứ chỉ hiện lúc runtime (F5, log, trình duyệt) |

Ranh giới này quan trọng: MCP đọc **model**, không đọc **giao diện**. Hai lần sai tên menu ở
§0.1 của [studio-pro-traps.md](references/studio-pro-traps.md) đều là thứ MCP **không** cứu được —
vẫn phải hỏi người dùng hoặc dựa vào ảnh chụp.

> Trước lần gọi MCP đầu tiên trong phiên, đọc resource `mendix://studio-pro/system-prompt`
> theo yêu cầu của server.

---

## 1. Quy tắc trình bày — quan trọng nhất trong skill này

Người dùng đã nêu rõ: **phần thao tác không được xen lẫn phần giải thích.** Trộn hai
thứ làm các bước cần bấm trở nên khó đọc, và người đang ngồi trước Studio Pro thì cần
bấm, không cần đọc luận văn.

### Bố cục bắt buộc cho mọi câu trả lời có thao tác Mendix

```
### Thao tác

Mở:    <đường dẫn .mpr tuyệt đối>
Branch: <tên branch>
Mục tiêu: <một câu, tối đa 15 từ>

1. <một hành động>
2. <một hành động>
3. <một hành động>

Báo lại: <đúng một thứ cần trả lời>

---

### Giải thích

<viết tự do ở đây>
```

### Luật cho khối "Thao tác"

| Luật | Vì sao |
|---|---|
| **Mỗi bước = một hành động**. Không gộp hai việc vào một dòng | Đọc lướt được khi mắt đang nhìn màn hình khác |
| **Không có chữ "vì", "để", "do", "bởi"** trong khối thao tác | Đó là giải thích, thuộc khối dưới |
| **Đường dẫn menu đầy đủ**: `Right-click module → Add → Microflow` | Không bắt người dùng đi tìm |
| **Giá trị chính xác**, đóng khung `code`. Không viết "đặt tên phù hợp" | Mơ hồ là nguồn của sai khác |
| **Không nêu phương án thay thế** trong khối thao tác | Chọn giúp; bàn phương án ở khối giải thích |
| **Tối đa 7 bước** một khối. Dài hơn thì tách nhóm, có điểm dừng ở giữa | Quá 7 bước thì mất dấu |
| **Kết thúc bằng đúng MỘT thứ cần báo lại** | Nhiều câu hỏi cùng lúc làm chậm vòng lặp |
| Nêu **kết quả nhìn thấy được** chỉ ở bước cần người dùng xác nhận | Để biết mình làm đúng chưa |

### Luật về tên menu, nút và trường

Khối "Thao tác" đòi **giá trị chính xác**. Nhưng giá trị chính xác **sai** còn tệ hơn giá trị
mơ hồ: người dùng đi tìm một mục không tồn tại, rồi mất niềm tin vào cả khối.

| Nguồn | Được dùng làm giá trị chính xác? |
|---|---|
| Đã thấy trong ảnh chụp của người dùng ở phiên này | **Có** |
| Ghi trong [studio-pro-traps.md](references/studio-pro-traps.md) §0 (đã kiểm chứng ở dự án này) | **Có** |
| Tài liệu chính thức docs.mendix.com, đúng version | Có, nhưng ghi rõ nguồn |
| Bài blog, Medium, câu trả lời forum | **Không** — chỉ dùng để hiểu bối cảnh |
| Trí nhớ về phiên bản cũ | **Không** |

Khi không chắc tên chính xác: **nói thẳng là không chắc**, mô tả vị trí và chức năng, rồi hỏi
người dùng đọc hộ tên thật. Một vòng hỏi-đáp rẻ hơn một lần đi tìm mục không tồn tại.

**Khi phát hiện tên sai**: sửa ngay vào §0 của `studio-pro-traps.md` cùng phiên, không để tới cuối.
Skill này chỉ có giá trị nếu nó tích luỹ được thứ đã kiểm chứng.

### Luật cho khối "Giải thích"

- Đặt **sau** khối thao tác, ngăn bằng `---`.
- Nói rõ: cái vừa làm là gì, vì sao chọn cách đó, cái gì sẽ hỏng nếu làm khác.
- Được dài. Người dùng đọc nó **sau khi đã bấm xong**, hoặc khi tò mò.
- Nếu không có gì đáng giải thích thì **bỏ hẳn khối này**, đừng viết cho có.

### Ví dụ đúng

> ### Thao tác
>
> Mở: `D:\mendix\NvmShopFloor\NvmShopFloor.mpr`
> Branch: `main`
> Mục tiêu: tạo microflow đọc danh sách WIP từ backend.
>
> 1. Right-click module `NvmShared` → **Add** → **Microflow**.
> 2. Đặt tên `DS_WipBoard_List`.
> 3. Kéo **Call REST service** từ Toolbox vào giữa Start và End.
> 4. Double-click nó, tab **General**, Location: `http://localhost:5080/pom/v1/WipBoard`.
> 5. Tab **Response**, chọn **Store in variable**, tên `HttpResponse`.
> 6. Bấm **OK**.
>
> Báo lại: Error List có bao nhiêu lỗi (View → Error List → Check now).
>
> ---
>
> ### Giải thích
>
> `DS_` là tiền tố cho microflow làm data source... (tiếp tục)

### Ví dụ SAI — đừng viết thế này

> 1. Tạo microflow mới, đặt tên `DS_WipBoard_List` — tiền tố `DS_` là quy ước cho
>    data source, vì Mendix không phân biệt được mục đích của microflow nên ta phải
>    tự đặt quy ước, và điều này quan trọng khi app lớn lên...

Bước và lý do dính vào nhau. Người đang bấm phải lọc chữ để tìm việc cần làm.

---

## 2. Preflight — làm trước mỗi lần chạm Mendix

1. Hỏi (hoặc xác nhận lại) **đường dẫn `.mpr` tuyệt đối** và **branch đang mở**. Tên app
   trong launcher của Studio Pro **không phải định danh** — nhiều bản sao cùng tên là chuyện thường.
2. Hỏi **Studio Pro version** nếu chưa biết. Dự án này dùng **11.12.3**, và plan M0 đã được sửa cho khớp (R5, 2026-08-27), xem
   [studio-pro-traps.md](references/studio-pro-traps.md) §0. `mx.exe` phải cùng version với bản đã sửa app.
3. Hỏi app có đang **dirty** không (Version Control → có thay đổi chưa commit). Nếu có,
   quyết định xem thay đổi đó có thuộc việc đang làm không **trước khi** đề xuất thao tác gì.
4. Đọc `docs/scope.md` §7.5 (Mendix ↔ Public Object Model) và plan của milestone hiện tại
   trước khi đề xuất thiết kế.

---

## 3. Ranh giới kiến trúc của dự án này

Ràng buộc **K10** trong `AGENTS.md`, không được lách:

> Mendix chỉ đi qua **Public Object Model** (OData, đọc) và **Command API** (REST, ghi).
> **Không** connection string tới PostgreSQL hay SQL Server của .NET.

Ba kênh, đúng như Opcenter Execution Foundation Starter Kit dạy:

| Kênh | Giao thức | Mendix dùng gì |
|---|---|---|
| Authentication | OIDC (Keycloak) | Module **OIDC SSO** từ Marketplace |
| Data query | OData v4, chỉ đọc | **Consumed OData Service** |
| Logic execution | REST POST | **Call REST service** trong microflow |

Nếu người dùng đề nghị nối thẳng Mendix vào database cho nhanh, nói rõ nó phá vỡ bounded
context và mọi thay đổi schema sẽ làm vỡ UI mà không ai biết — rồi đưa cách đúng.

### 3.1 Một câu để quyết mọi tranh cãi "cái này làm ở đâu"

> **Mendix render và thu thập. Backend quyết định.**

Lấy từ `docs/conventions/mendix-standard.md` §1 của CGVibe, và nó khớp chính xác với K10.

| Mendix ĐƯỢC | Mendix KHÔNG được |
|---|---|
| Hiển thị dữ liệu backend trả về | Tính SoC, tính grade, tính yield |
| Kiểm tra một ô đã điền chưa | Quyết định một lot có được release không |
| Kiểm tra định dạng serial 16 ký tự | Quyết định hold có cascade xuống đâu |
| Điều hướng giữa các page | Là system of record cho dữ liệu sản xuất |
| Cache dữ liệu backend để hiển thị | Coi cache đó là nguồn sự thật |

Đang viết một microflow có biểu thức số học liên quan tới đo lường sản xuất hay quyết định
chất lượng thì **dừng lại** — chỗ đó thuộc về .NET.

### 3.2 Page access KHÔNG phải ranh giới bảo mật

Cấu hình role cho page trong Mendix chỉ **ẩn điều hướng**, không bảo vệ dữ liệu. Backend
authorize mọi request bất kể Mendix nghĩ gì. **Không bao giờ coi một nút bị ẩn là biện pháp bảo
vệ.** Đây là hệ quả trực tiếp của K10: nếu Mendix chỉ đi qua OData/REST thì mọi quyết định
quyền nằm ở đầu bên kia.

Hệ quả liên quan cho M4: token không bao giờ được lưu trong entity Mendix hay bất cứ chỗ nào
trình duyệt thấy được.

---

## 4. Thang kiểm chứng — đừng nhầm mức

| Mức | Chạy gì | Chứng minh được gì |
|---|---|---|
| Tài liệu đang mở | Studio Pro tự báo lỗi khi gõ | Cú pháp của đúng document đó |
| Toàn app | **View → Error List → Check now** | Nhất quán model, lỗi chặn deploy |
| Headless | `mx check <app>.mpr` cùng version | Lỗi/cảnh báo tái lập được, chạy trong script |
| Runtime | **F5** rồi đọc console log | App deploy và khởi động được |
| Đầu-cuối | Đăng nhập thật, bấm thật, gọi backend thật | Tính năng chạy qua ranh giới thật |
| Giao hàng | Commit + push, kiểm branch đích | Người khác lấy được đúng thứ đã kiểm |

**"Không thấy popup nào" không phải bằng chứng.** Luôn hỏi **số lỗi cụ thể** trong Error List.

### 4.1 Browser pane bị ẩn KHÔNG kiểm chứng được app Mendix

Nếu browser pane không được hiển thị, `document.visibilityState` là `"hidden"` và trình duyệt
**ngưng hẳn** `requestAnimationFrame` (không phải chỉ bóp chậm như timer). Client Mendix lại
**gán việc mở page và dựng dialog vào một frame**. Hệ quả:

- Một `.click()` hoặc chuỗi pointer event gửi tới nút Mendix **đi tới nơi và không làm gì cả**.
  Không có lỗi, không có gì trong console. Nút báo `disabled: false`, class đúng, và modal
  không bao giờ xuất hiện.
- Mọi phép đo thời lượng (toast, skeleton, curtain) đang đo cái throttle, không đo app.

**Đây là cái giả dạng lỗi ứng dụng.** Kiểm chứng bằng `document.visibilityState` **trước** khi
kết luận app hỏng. Muốn thao tác thật thì pane phải được hiển thị; nếu không, chuyển sang MCP
(model) hoặc handoff cho người dùng, và **nói rõ là bị chặn** thay vì báo đã thử và thất bại.

**Đã đo trực tiếp trên NovaVolt (2026-08-26)** — không còn là giả thuyết kế thừa:

```js
(async () => {
  const rafOk = await new Promise(res => {
    let done = false;
    requestAnimationFrame(() => { done = true; res(true); });
    setTimeout(() => { if (!done) res(false); }, 1500);
  });
  return { rafOk, vis: document.visibilityState };
})()
//  →  { rafOk: false, vis: "hidden" }
```

`screenshot` cũng báo thẳng: *"the Browser pane is not displayed, so the page is not compositing
frames"*. Chạy đoạn probe này **trước** mọi kịch bản tự động hoá UI Mendix. `rafOk: false` nghĩa
là mọi click sau đó vô nghĩa — dừng lại và nói ra, đừng click rồi báo "không có gì xảy ra".

Đường vòng khi bị chặn: `curl` thẳng vào endpoint của runtime. Xem
[studio-pro-traps.md](references/studio-pro-traps.md) §0.19 cho ví dụ kiểm chuỗi SSO.

Hai bẫy đi kèm, cùng nguồn CGVibe:

- **Mendix trả `200` cho MỌI đường dẫn `/p/*`** — cùng một client bootstrap. `curl`, `fetch`
  hay status code **không** cho biết một page có tồn tại hay không. Phải mở trong một session
  có role phù hợp mới biết.
- Mendix từ chối vì module role trả `{"error":{"code":"404","message":"Resource not found."}}`
  **trước khi** microflow chạy — trông hệt như "không tìm thấy" thật.

---

## 5. Bẫy đã được kiểm chứng

Tổng hợp từ ledger của dự án CGVibe (Studio Pro 11.12.1). Đọc
[studio-pro-traps.md](references/studio-pro-traps.md) để có bản đầy đủ. Bảy cái hay gặp nhất:

1. **F4 là *Synchronize App Directory*, không phải kiểm tra lỗi.** Muốn kiểm nhất quán:
   Error List → Check now. **F5** chạy app.
2. **`mx check` đọc file `.mpr` trên đĩa, còn Studio Pro giữ tài liệu đang sửa trong bộ nhớ.**
   Một tab chưa lưu làm `mx check` báo lỗi `CE1613` "no longer exists" về những thứ **đang tồn tại**.
   Luôn **File → Save All** trước, và nhìn tab bar: **chấm tròn trên tab = chưa lưu**.
3. **Trang mới có thể có danh sách allowed roles RỖNG.** Rỗng không phải lỗi model, nên nó
   qua được Error List *và* `mx check`, rồi 404 ở F5. Kiểm roles bằng mắt cho mọi page và
   navigation microflow mới.
4. **Version Control → Merge Changes Here** merge branch nguồn **vào branch đang mở**.
   Không đi đường **Manage Branch Lines**.
5. **Không bao giờ đề xuất "Revert All Changes"** để thoát khỏi trạng thái dirty. Đó là
   thao tác mất dữ liệu.
6. **`.mpk` mới copy vào `widgets/` là vô hình với model cho tới khi F4.**
7. **Module chưa có module role thì mọi document trong đó sinh lỗi hàng loạt** — chữ ký là
   cụm *"(with no roles defined in module X)"*. Một gốc, hàng chục lỗi. Tạo module role rồi
   map vào user role, cả cụm biến mất. MCP **không** tạo được module role.

---

## 6. Quy ước đặt tên

Theo `docs/scope.md` và chuẩn Mendix. Dùng đúng, đừng sáng tạo:

**Microflow**: `{PREFIX}_{Đối tượng}_{Hành động}`

| Prefix | Dùng cho |
|---|---|
| `ACT_` | Hành động của button hoặc menu |
| `DS_` | Data source cho widget |
| `OCH_` | On-change |
| `VAL_` | Kiểm tra hợp lệ |
| `SUB_` | Microflow con, chứa logic |
| `BCO_` | Before-commit |

**Luật quan trọng**: `ACT_` và `DS_` **không chứa logic nghiệp vụ** — chỉ Client Activity
(mở/đóng page, hiện message) và lời gọi microflow khác. Logic nằm trong `SUB_`.

**Module**: `NvmShared` (dùng chung), `NvmShopFloor`, `NvmQuality`, `NvmTrace`.

---

## 7. Kết thúc và báo cáo

Khi người dùng báo đã làm xong, nêu tách bạch:

- Đã thay đổi document nào trong model;
- Error List bao nhiêu lỗi, `mx check` bao nhiêu;
- Có chạy F5 không, log nói gì;
- Đã commit chưa, đã push chưa, **đã merge vào branch đích chưa** — ba câu hỏi khác nhau;
- Cái gì chưa kiểm.

Không bao giờ đổi "feature branch sạch" thành "branch tích hợp đã có".

---

## 8. Định tuyến reference

- Bẫy Studio Pro đã kiểm chứng: [studio-pro-traps.md](references/studio-pro-traps.md)
- Mendix nói chuyện với backend .NET: [backend-integration.md](references/backend-integration.md)
- Agent tự ghi model bằng MCP (tầng ghi §0.1): [mcp-model-work.md](references/mcp-model-work.md)
