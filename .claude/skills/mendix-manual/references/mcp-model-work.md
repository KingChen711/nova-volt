# Studio Pro MCP — khi agent làm hộ

File này chỉ dùng cho **tầng ghi** ở [SKILL.md](../SKILL.md) §0.1: người dùng nói rõ trong lượt
hiện tại rằng *"làm hộ tôi phần này"*. Mặc định của dự án vẫn là người dùng tự bấm và agent viết
các bước — file này không thay đổi điều đó.

Tầng đọc (`list_modules`, `ped_read_document`, `pg_read_page`, `ped_check_errors`, `read_file`,
`glob`) luôn được dùng, kể cả khi người dùng tự thao tác. Phần lớn nội dung dưới đây phục vụ
tầng ghi, nhưng mục "Thang tin cậy", "Tên document" và "Giới hạn đã biết" áp dụng cho cả hai.

## Nguồn và độ tin cậy

Mỗi khẳng định dưới đây được gắn nhãn:

| Nhãn | Nghĩa |
|---|---|
| **[SERVER]** | Lấy từ `mendix://studio-pro/system-prompt` hoặc từ mô tả tool / `read_skill` của chính server đang chạy. Đây là nguồn chính thống. |
| **[NOVAVOLT]** | Đã tự kiểm chứng trên app NovaVolt, Studio Pro 11.12.3, 2026-08-26. |
| **[CGVIBE]** | Kế thừa từ `D:\code\CGV\.agents\skills\mendix-workflow`. Do người dùng tự đúc kết ở dự án khác, Studio Pro 11.12.1. **Chưa kiểm chứng lại ở đây.** Coi là giả thuyết đáng thử trước, không phải sự thật. |

Khi một mục **[CGVIBE]** được kiểm chứng hoặc bị bác bỏ ở dự án này, sửa nhãn ngay trong lượt
đó — cùng quy tắc với `AGENTS.md` §5.6.2.

> **[SERVER]** Trước lần gọi MCP đầu tiên trong phiên, đọc `mendix://studio-pro/system-prompt`.
> Server yêu cầu điều này và nó chứa toàn bộ luật schema bên dưới ở dạng đầy đủ.

## Catalog tool thật của bản đang chạy

**[NOVAVOLT]** Studio Pro 11.12.3 phơi ra **18** tool `mcp__mendix__*`:

| Nhóm | Tool |
|---|---|
| Khám phá | `list_modules`, `ped_find_document`, `ped_list_folder` |
| Đọc model | `ped_read_document`, `pg_read_page`, `ped_get_schema` |
| Ghi model | `ped_create_document`, `ped_create_module`, `ped_update_document`, `pg_patch_page` |
| Kiểm tra | `ped_check_errors` |
| File ngoài model | `glob`, `read_file`, `write_file` |
| Kiến thức | `read_skill`, `search_mendix_knowledge_base` |
| Marketplace | `install_marketplace_module` |
| View entity / OQL | `oql_generate` |

> [!warning] Vắng mặt trong danh sách lúc đầu phiên KHÔNG chứng minh là không tồn tại
> **[NOVAVOLT]** Mục này ban đầu ghi *"bản 11.12.3 không có `oql_generate`, đừng gọi tên tool
> đó"* — dựa vào danh sách deferred tool lúc mở phiên, đếm được đúng 17. Giữa phiên, harness
> phơi thêm `mcp__mendix__oql_generate`. Khẳng định cũ **sai**, và [CGVIBE] nói 18 tool là đúng.
>
> Bài học chung, không riêng gì tool này: danh sách tool được nạp **dần**. Muốn kết luận một
> tool không tồn tại thì phải `ToolSearch` tìm tên nó rồi mới nói, đừng suy từ danh sách đang
> thấy. Cùng họ với bẫy `ped_get_schema` có schema mà `ped_read_document` không đọc được — hai
> nguồn khác nhau, đừng lấy cái này chứng minh cái kia.

**[NOVAVOLT]** Chỉ hai module ghi được: `MyFirstModule` và `NvmShared`. Mọi module còn lại
(`System`, `OIDC`, `Administration`, `Atlas_*`, `DataWidgets`, `CommunityCommons`,
`UserCommons`, `NanoflowCommons`, `WebActions`, `FeedbackModule`) là read-only. **[SERVER]**
Không bao giờ sửa document trong module không ghi được, và chỉ đọc sâu vào chúng khi người dùng
yêu cầu hoặc khi schema nhắc đích danh tên module đó.

## Quy trình bắt buộc trước MỌI create/update

**[SERVER]** Đúng thứ tự này, không bỏ bước:

1. **Kiểm tra khả thi.** Việc cần làm có nằm trong các thao tác an toàn không? Nếu không, nói
   cho người dùng các bước thủ công thay vì cố ép.
2. **`read_skill` các skill liên quan** trước khi build, không phải sau. Skill chứa ràng buộc
   bắt buộc, không phải mẹo. Không nạp lại skill đã nạp trong phiên.
3. **`ped_find_document`** — có kết quả trùng thì đọc rồi *update*; chỉ *create* khi không có.
4. **Chọn thư mục**: nạp skill `folder-structure`, gọi `ped_list_folder` trên module đích, suy
   ra `folderPath` từ cấu trúc đang có. Không bao giờ mặc định vứt vào gốc module.
5. **`ped_read_document`** trạng thái hiện tại (nếu update).
6. **`ped_get_schema` cho MỌI type sắp tạo/thêm.** Không bao giờ suy cấu trúc từ kết quả đọc.
   Gộp tất cả type vào một lời gọi.
7. **Đọc kỹ mô tả property.** Mô tả càng dài càng quan trọng. Để ý `critical: true`. Chỉ set
   những property mà schema có nhắc.
8. **Dựng payload** theo luật schema bên dưới, rồi create/update.
9. **`ped_check_errors` một lần duy nhất, sau TẤT CẢ thay đổi** — không chạy giữa các bước.

**[SERVER]** Gộp lời gọi độc lập vào cùng một lượt: `ped_read_document` nhiều path,
`ped_get_schema` nhiều type, và `read_skill` nên đi chung một turn.

### Skill của server, nạp trước khi chạm

**[SERVER]** `folder-structure` (mọi lần tạo document) · `domain-model-common` (entity) ·
`microflow-common` + `microflow-expressions` + `microflow-xpath` (microflow) ·
`page-gen-common` (page) · `navigation` (mọi thay đổi navigation) · `view-entities` (OQL) ·
`workflow-common` / `workflow-update` · `validation-microflow` · `database-connector-common` ·
`javascript-action` · `theming` / `design-properties` / `glyph-icons`.

**[SERVER]** Định tuyến prefix microflow → thư mục: `VAL_`, `SUB_` và các prefix tầng dữ liệu
khác → `<Entity>` (hoặc `<Entity>/Events` nếu là event handler); `ACT_`, `DS_`, `OCH_`, `OLE_`,
`NAV_` → `<Entity>` hoặc `<Entity>/Pages`. **ACT_ và DS_ không được chứa business logic** — chỉ
Client Activity và Microflow Call.

## Hệ thống schema — chỗ sai nhiều nhất

**[SERVER]** `ped_get_schema` trả về bốn loại:

- **`$constructor`** — hình dạng lúc *tạo*, đã làm phẳng. **Tên property có thể KHÁC với lúc
  đọc.** Constructor `{"objects": [...]}` đọc lại thành `{"objectCollection": {"objects": [...]}}`.
  Tạo thì dùng tên của constructor; update thì dùng path của bản đọc. Thêm sub-element bằng
  `ped_update_document` cũng theo schema constructor.
- **`$element`** — cấu trúc thật, đầy đủ wrapper. Dùng khi type không có constructor.
- **`$object`** — object giá trị nằm trong constructor. **TUYỆT ĐỐI không gắn `$Type`** — gắn
  vào là parser hỏng. Element thì luôn có `$Type`.
- **`$abstractElement`** — chọn **một** type cụ thể trong `allowedTypes` rồi lấy schema của nó.
  Không đoán.

**[SERVER]** Cú lật kinh điển: `flows` tạo *bên trong* constructor microflow là `$object`
(không `$Type`); cũng flow đó thêm sau bằng `ped_update_document` lại là element
`Microflows$SequenceFlow` (**phải** có `$Type`).

Luật payload **[SERVER]**:

- `$Type` cho mọi constructor/element; **không bao giờ** `$ID`.
- Tham chiếu theo `referenceType`: `by-id` → `$id(/path)` **chỉ số từ 0**
  (`$id(/entities/3)` là entity thứ **tư**); `by-name` → `"Module.Element"`; `local-by-name`
  → `"Element"`. Không lẫn lộn giữa ba kiểu.
- Trong payload *tạo*, `$id` đi theo cấu trúc **constructor** (`$id(/objects/0)`); trong payload
  *update* trỏ tới element có sẵn thì đi theo cấu trúc **bản đọc**
  (`$id(/objectCollection/objects/0)`).
- Hình dạng nguyên thủy: `point` → `{x, y}`, `size` → `{width, height}`, `blob` → `""`,
  `anyOfValues` → chép **nguyên văn**, phân biệt hoa thường.

## set / add / remove — và cái bẫy index ngược nhau

**[SERVER]** `set` không hoạt động đồng nhất:

| Loại property | `set` |
|---|---|
| Nguyên thủy (string, số, bool, enum, reference, `point`/`size`) | Luôn được, dù đang rỗng hay đã có giá trị |
| Element-valued (object lồng có `$Type` riêng) | **Chỉ khi đang `null`/`undefined`.** Đã có giá trị thì bị từ chối: *"Cannot set element-valued property: it already has a non-null value."* |
| Mảng / list | **Không bao giờ.** Dùng `add`/`remove` |

**[SERVER]** Khi element-valued đã có giá trị mà cần đổi, theo thứ tự ưu tiên:

1. **Set một primitive BÊN TRONG element đó.** Thường thứ cần đổi chỉ là một trường con.
2. Nếu thật sự cần đổi sang `$Type` khác thì phải xóa và tạo lại element chứa nó — đây là
   **thao tác radical**, phải dừng lại hỏi người dùng.

### Hai tool, hai luật index NGƯỢC nhau — **[NOVAVOLT]**

Đây là chỗ dễ sai nhất khi vừa sửa page vừa sửa microflow trong một lượt. Kiểm chứng bằng chính
mô tả schema của hai tool trên bản 11.12.3:

| | `ped_update_document` | `pg_patch_page` |
|---|---|---|
| Nhiều `remove` cùng một mảng | Server **tự sắp lại** theo index giảm dần. Mọi `index` bạn truyền đều tính theo trạng thái **hiện tại lúc đọc**. **Không được** tự bù trừ index. | Server **không** sắp lại. Bạn **phải** tự viết các `remove` theo **index giảm dần** (cao nhất trước). |
| Thêm vào mảng | `add` với path trỏ vào mảng, **không kèm index** | `op: "add"` với path kết thúc bằng `/-`. Không bao giờ `replace` cả mảng |

**Sửa [CGVIBE]:** file CGVibe nói *"NEVER batch add + remove on the same path in one call"* cho
`ped_update_document`. Mô tả tool của server nói ngược lại — nó chủ động sắp lại chính các thao
tác add/remove cùng path. Luật CGVibe quá chặt và nhiều khả năng bắt nguồn từ nhầm lẫn với
`pg_patch_page`, nơi luật đó đúng.

**[SERVER]** Thứ tự vẫn quan trọng ở chỗ khác: thêm element được tham chiếu **trước** element
tham chiếu tới nó (entity trước association).

## Thao tác an toàn và thao tác radical

**[SERVER]**

- **An toàn, luôn được:** thêm/tạo element và document; set property nguyên thủy; xóa **duy
  nhất** element mà lỗi hoặc người dùng gọi đích danh.
- **Radical, phải có người dùng đồng ý rõ ràng:** xóa element không được nhắc tới; xóa hàng
  loạt; dựng lại element/document từ đầu; tái cấu trúc flow/layout; hoàn tác thay đổi của người
  dùng; **bất kỳ thay đổi nào mà bạn không chắc giữ đúng ý người dùng**.

Luật này chồng lên chứ không thay thế `AGENTS.md` §1.1 và §5.6: "làm hộ tôi phần này" cho phép
đúng phần đó, không suy rộng sang lượt sau.

## Error protocol — luật một-lần-sửa

**[SERVER]** Cứng, không ngoại lệ:

1. `ped_check_errors` sau khi xong **tất cả** thay đổi. Sạch → xong.
2. Có lỗi → phân tích → **một** lời gọi `ped_update_document` với bản sửa tối thiểu.
   Nếu bản sửa là radical → nhảy thẳng xuống bước 4.
3. `ped_check_errors` lại. Sạch → xong.
4. Còn lỗi → **DỪNG và báo cáo**: *"Gặp lỗi X. Để xử lý, cần làm Y."* Không gọi
   `ped_update_document` lần hai. Không thử hướng khác. Chờ người dùng.

**[SERVER]** Nguyên tắc tối thiểu: mọi thay đổi phải là thay đổi nhỏ nhất giải quyết được lỗi
hoặc yêu cầu. Đụng vào thứ ngoài phạm vi đó gây lỗi dây chuyền. Không hỏng thì đừng sửa.

## Tên document — nguồn lỗi 404 phổ biến nhất

**[SERVER]**

| Loại | `documentName` |
|---|---|
| Domain model | **Chỉ tên module** (`"NvmShared"`). Type là `DomainModels$DomainModel`. Luôn tồn tại, không bao giờ tạo mới. Không dùng type này với `ped_find_document`. |
| Singleton cấp project (vd `Navigation$NavigationDocument`) | **Bỏ trống hẳn** field này |
| Còn lại | Tên đầy đủ `"Module.DocumentName"` |

**[NOVAVOLT]** Nhưng `ped_check_errors` lại **bắt buộc** có `documentName` (schema đánh dấu
`required`). Nên các singleton cấp project **không kiểm tra được** bằng tool này — xác nhận
khẳng định [CGVIBE]. Sau khi đổi Navigation: đọc lại để xác nhận, rồi chạy **View → Error List
→ Check now** trong Studio Pro cho toàn app. Đừng lấy kết quả check của mấy page/microflow được
tham chiếu làm bằng chứng cho Navigation.

## Từ khóa cấm

**[SERVER]** Không dùng ở **bất kỳ kiểu viết hoa/thường nào**, kể cả khi người dùng yêu cầu —
phải tự đổi sang tên khác (`Type` → `TrainingType`):

Mọi từ khóa Java, cộng với: `type`, `MendixObject`, `__filename__`, `changedby`, `changeddate`,
`context`, `createddate`, `currentUser`, `empty`, `guid`, `id`, `object`, `owner`,
`submetaobjectname`, `con`, và các biến định sẵn `currentDeviceType`, `currentIndex`,
`currentSession`, `latestError`, `latestSoapFault`, `latestHttpResponse`.

## Page — chỉ `pg_*`, tuyệt đối không `ped_*`

**[SERVER]** Khi việc cần tới một page, tự động làm ngay, không hỏi: đọc skill `page-gen-common`
trước mọi thay đổi; dùng **duy nhất** các tool `pg_*` để đọc/tạo/sửa page; **không bao giờ** dùng
`ped_*` cho page. Sau khi xong toàn bộ mới chạy `ped_check_errors` và theo error protocol.

**[NOVAVOLT]** Hình dạng gốc của page, xác nhận từ schema `pg_patch_page`: bắt buộc `title`,
`layout`, `parameters`, `widgets`; tùy chọn `variables`. **Không có `url`, không có
`allowedRoles`.**

**[CGVIBE]** Nhưng hai thứ đó *lại* set được bằng `ped_update_document` — và đây là chỗ dễ tự
bắn vào chân:

- `/allowedRoles` set được, và **phải** set. Xem bẫy "page rỗng role" bên dưới.
- `/url` set được như một string thường (lưu **không** kèm tiền tố `/p/`: `station/overview`
  cho ra `/p/station/overview`) — **nhưng nó không tạo ra `Url$StaticUrlSegment` đi kèm.**
  Kết quả: `mx check` và Error List đều sạch, còn page thì 404 khi mở trực tiếp và bị đá về
  trang chủ. **Đặt URL của page là handoff qua Studio Pro → Properties.** Đừng dùng đường tắt
  này chỉ vì nó chạy.

- Tạo page mới: **một** op duy nhất `{"op":"replace","path":"","value":{<LightPage đầy đủ>}}`.
- Sửa page có sẵn: các op `add`/`replace`/`remove` nhắm đúng path; path phải trỏ tới phần tử
  **đang tồn tại**; `-` ở cuối để append. Không root-replace để sửa vặt.
- `patches` là mảng JSON thật, **không phải chuỗi đã stringify**.

**[CGVIBE]** Kỷ luật null — nguồn lỗi validation số một: property bắt buộc kiểu string/bool/số →
`""` / `true|false` / `0`, **không bao giờ** `null`. Property tùy chọn mà type không có `/null`
→ **bỏ hẳn property đó**, không đặt `null`.

**[CGVIBE]** Luật không-bịa: mọi `$Type`, giá trị enum và tên property phải đến từ (a) file
reference đã đọc trong phiên, (b) file VFS đã đọc trong phiên, hoặc (c) chính JSON của page.
Không suy ra type theo mẫu, không đoán giá trị enum vì thấy có Primary/Secondary thì chắc có
Tertiary. Không chắc → dừng và hỏi, nói rõ giá trị nào và mong đợi tìm thấy nó ở file nào.

## Microflow — hai luật sắt khi sửa flow có sẵn

**[CGVIBE]**, chưa kiểm chứng ở NovaVolt nhưng nhất quán với mô hình PED:

1. **Tự động xóa theo:** bỏ một object khỏi `/objectCollection/objects` sẽ xóa **toàn bộ** flow
   nối vào nó. Đừng xóa flow bằng tay. Ngược lại thì không: xóa flow không xóa object.
2. **Đọc lại sau mỗi lần mutate:** mọi add/remove đều dịch index. Gọi `ped_read_document` trước
   lần `ped_update_document` kế tiếp, nếu không `$id(/objects/N)` sẽ trỏ nhầm. Index trong JSON
   **không** theo thứ tự nhìn thấy trên canvas.

**[CGVIBE]** `allowedModuleRoles` **không có** trong `$constructor` của microflow. Mọi microflow
tạo qua MCP khởi đầu không có role nào và dính `CE0106` ngay khi có thứ gì phía client gọi nó.
Property này set được *sau khi* tạo — nên luôn kèm một `ped_update_document` thêm role ngay
trong cùng lượt, **trước** `ped_check_errors`.

Chi tiết layout math, split/caseValue, loop, biến trong scope: nạp `microflow-common` của
server. Đừng dựng microflow từ trí nhớ.

## Bẫy im lặng — riêng của việc GHI bằng MCP

Nhóm bẫy "qua mọi lần check rồi hỏng lúc chạy" nằm ở
[studio-pro-traps.md](studio-pro-traps.md) §2–§4 và áp dụng cho cả người bấm tay lẫn agent —
**đọc §2 trước mỗi lần tạo page/microflow bằng MCP**, đặc biệt "Danh sách allowed roles rỗng"
và "Entity access biến `setValue` thành no-op im lặng". Không chép lại ở đây để hai file không
trôi lệch nhau.

Bốn cái dưới đây **chỉ xảy ra khi agent ghi bằng MCP**, nên chúng ở đây:

- **Đọc lại đúng property vừa ghi. Luôn luôn.** **[CGVIBE]** `returnType` truyền qua
  `ped_create_document` bị rớt ở hai microflow rồi lại áp dụng ở bốn cái khác **trên cùng một
  bản Studio Pro**. Nên quy tắc bền vững không phải "property X hay bị rớt" — mà là read-back.
  Kết quả trả về không phân biệt "đã áp dụng" với "đã âm thầm bỏ qua".
- **Document tạo bằng MCP khởi đầu với danh sách role RỖNG**, khác với khi tạo trong Studio Pro
  UI. Đây là lý do bẫy allowed-roles ở traps §2 gặp thường xuyên hơn hẳn khi agent làm hộ.
  Tạo xong page/microflow → đọc lại role ngay trong cùng lượt, **trước** `ped_check_errors`.
- **Xóa attribute bằng MCP để lại access rule mồ côi.** **[CGVIBE]** Server xóa được attribute
  nhưng từ chối `DomainModels$MemberAccess` và `DomainModels$AccessRule` (*"cannot be removed.
  Do not try removing elements of this type again"*). Dọn phần còn lại là handoff.
- **PED *liệt kê* được vài loại document mà nó không *đọc* được.** **[CGVIBE]**
  `Rest$PublishedRestService`: `ped_find_document` liệt kê nó và `ped_get_schema` trả schema
  đầy đủ, nhưng `ped_read_document` trả `Unknown document type` cho đúng chuỗi đó.
  `Pages$Layout` cũng vậy (*"Did you mean: Pages$Page?"*). **Đừng suy ra "không tồn tại" từ một
  lần đọc thất bại** — dùng `mx dump-mpr` thay thế.

Còn phần Published/Consumed REST (error handling ở chuột phải, allowed roles là thuộc tính của
service, module role vs user role, tên query parameter khớp đúng chữ): traps §3 và §4 đã có
đủ. Điều **[CGVIBE]** bổ sung riêng cho MCP là `errorHandlingType` của
`Microflows$RestCallAction` **không set được bằng tool** — constructor bỏ qua nó và `set` sau
đó bị từ chối vì đã có giá trị non-null — nên mọi Call REST do agent tạo đều kẹt ở `Rollback`.
Đổi sang **Custom without rollback** luôn là handoff.

## Giới hạn đã biết — kế thừa [CGVIBE], chưa kiểm chứng ở NovaVolt

Coi đây là danh sách "thử cái này trước khi tự nghi ngờ mình", không phải sự thật đã xác lập.

- **Không có tool xóa document.** Catalog có create/read/update/check nhưng không có delete.
  Xóa page/microflow/Java action luôn là handoff. Khi tập cần xóa có tham chiếu vòng, đưa người
  dùng **thứ tự** xóa — Studio Pro từ chối xóa document còn bị trỏ tới, nên bóc từ ngoài vào.
  *(Ghi chú **[SERVER]**: system-prompt lại nói có hỗ trợ "REMOVALS of elements/documents". Hai
  nguồn mâu thuẫn; catalog tool là thứ quan sát được, nên theo catalog.)*
- **Nanoflow không phải một document type.** `Microflows$Nanoflow` trả `Unknown document type`.
  Published REST service, JSON structure, import mapping cũng vậy. Đọc chúng bằng
  `mx dump-mpr --unit-type=... --module-names=...` thay vì đoán.
- **Sửa bằng MCP chưa nằm trên đĩa cho tới khi người dùng Save All.** MCP đổi model **sống**
  trong Studio Pro; `.mpr` trên đĩa chưa đổi. Hệ quả: `mx check` chạy giữa lúc sửa bằng MCP và
  lúc save trả về "xanh" vô nghĩa. Bảo người dùng **Save All** trước, hoặc check sau khi F5.
  `ped_check_errors` đọc model sống nên nó lệch với đĩa là bình thường, không phải bug.
- **Một create/add có thể báo `SUCCESS` mà âm thầm bỏ qua property mà constructor không khai
  báo.** Không có gì trong kết quả phân biệt "đã áp dụng" với "đã bỏ qua". Nên **đọc lại đúng
  property đó** sau khi ghi bất kỳ giá trị nào schema không liệt kê.
- **Payload lồng nhau quá lớn có thể không tới được server dưới dạng JSON hợp lệ.** Cùng cấu
  trúc đó chia nhỏ ra thì thành công. Nghi ngờ điều này khi chuỗi biểu thức có chứa dấu nháy.
- **`Pages$PageParameterMapping` không xóa được.** Bỏ parameter của một page sẽ làm mồ côi mọi
  `ShowPageAction` truyền nó vào.
- **`JavaActions$JavaAction` là read-only** ở phía model. Mã Java dưới `javasource/` vẫn sửa
  được như file thường; chỉ chữ ký trong model là bị chặn.
- **Access rule của entity ghi được**, nhưng luôn để lại `CE0066 Entity access is out of date`
  — cờ này chỉ được xóa bằng nút **Update security** trong domain model editor.
  **[NOVAVOLT]** Đã kiểm chứng trên 11.12.3: không chỉ khi *thêm attribute*, mà cả khi **thêm
  một access rule mới** bằng `ped_update_document`. `ped_check_errors` trả đúng một dòng:

  ```
  'NvmShared': - Entity access is out of date. Please update security by clicking the
  'Update security' button in the domain model editor.
  ```

  Đây là **lỗi**, không phải cảnh báo — nó chặn deploy. Nên **mọi lần agent ghi access rule
  bằng MCP đều kết thúc bằng một handoff một-nút**: mở domain model của module đó, bấm
  **Update security**. Báo trước cho người dùng ngay lúc ghi, đừng để họ thấy lỗi rồi mới nói.
- **Security cấp app** (map module role → user role, page/entity access) **không** với tới được
  qua MCP. Nói thẳng ra và đưa handoff. Dùng
  `mx export-security-overview -t json -o <file> <app.mpr>` để *đọc* kết quả.
  **[NOVAVOLT]** Đã kiểm chứng và **module role cũng vậy** — xem mục riêng bên dưới.
- **Đổi branch, pull, commit, push, merge** nằm ngoài bề mặt MCP hoàn toàn.

## Module role: có schema nhưng KHÔNG có document — **[NOVAVOLT]**

Đây là cái bẫy tốn thời gian nhất của việc tạo document trong một module mới.

```
ped_get_schema(['Security$ModuleSecurity'])  →  trả schema đầy đủ, có mảng moduleRoles
ped_read_document(Security$ModuleSecurity, 'NvmShared')  →  ERROR: Unknown document type
```

**`ped_get_schema` trả về schema KHÔNG chứng minh document type đó đọc/ghi được.** Hai tool
tra hai bảng khác nhau. Cùng mẫu với `Security$ProjectSecurity` ở
[studio-pro-traps.md](studio-pro-traps.md) §0.9 — nhưng ở đó ít nhất `ped_get_schema` cũng
không có gì, còn ở đây schema *có*, nên rất dễ tưởng là làm được.

### Hệ quả: thứ tự bắt buộc khi module chưa có module role

Module mới tạo (kể cả tạo bằng `ped_create_module`) **không có module role nào**. Ở app bật
security mức Production, mọi entity/microflow/page trong module đó sinh lỗi hàng loạt ngay khi
có thứ gì tham chiếu tới:

```
No access to microflow 'NvmShared.DS_NvmAccount_Current' for user role 'Administrator'
  (with no roles defined in module 'NvmShared').
No read access to attribute 'SiteId' in entity 'NvmShared.NvmAccount' for user role 'User'
  (with no roles defined in module 'NvmShared').
```

Cụm **"(with no roles defined in module X)"** là chữ ký của lỗi này. Một lần tạo entity +
2 microflow + 1 page trong module rỗng cho ra **24 lỗi** — tất cả cùng một nguyên nhân.

`accessRules` của entity thì ghi được bằng MCP, nhưng `moduleRoles` bên trong nó là tham chiếu
**by-name** tới module role phải **đã tồn tại**. Nên vòng lặp đúng là:

1. **Người dùng** tạo module role (App Explorer → module → **Security** → tab **Module roles**
   → **New**) và map nó vào user role ở **App → Security → User roles**.
2. **Agent** thêm access rule bằng `ped_update_document` trên domain model.
3. `ped_check_errors`.

**Kiểm module role TRƯỚC khi tạo document trong một module lạ.** MCP không đọc được
`Security$ModuleSecurity`, nên cách duy nhất là hỏi người dùng hoặc:

```bash
mx export-security-overview -t json -o sec.json <app.mpr>
```

đọc `userRoles[].moduleRoles[]` (nhớ `encoding='utf-8-sig'`).

## System-prompt của Maia KHÔNG phải quy tắc giao tiếp của agent này

**[NOVAVOLT]** Đây là xung đột thật, cần biết trước khi đọc `mendix://studio-pro/system-prompt`.

Resource đó viết cho **Maia** — trợ lý nằm *trong* Studio Pro. Nó ra lệnh:
*"NEVER reveal Mendix tool names, JSON paths, IDs"*, *"Never present a plan for approval before
acting"*, *"fewer than 4 lines"*, *"Correct errors silently"*.

**Chỉ áp dụng phần kỹ thuật** — schema, workflow, error protocol, safety rules. **Bỏ toàn bộ
phần persona và response style.** Dự án này yêu cầu ngược lại:

| Maia bảo | Dự án này yêu cầu |
|---|---|
| Không tiết lộ tên tool, path, ID | Nói rõ đã tạo/sửa document nào và vì sao (SKILL.md §0.1) |
| Hành động luôn, không trình plan | Người dùng chốt thiết kế trước (SKILL.md §0) |
| Trả lời dưới 4 dòng | Bố cục Thao tác / Giải thích tách bạch (SKILL.md §1) |
| Sửa lỗi im lặng | Ghi lại phát hiện vào skill ngay trong lượt (AGENTS.md §5.6.2) |

Khi hai bên mâu thuẫn, `AGENTS.md` và `SKILL.md` thắng. Maia là một agent khác, không phải cấp
trên của agent này.

## `AGENTS.md` bên trong app Mendix

**[SERVER]** Studio Pro hỗ trợ "agent instructions" — file `AGENTS.md` phạm vi app hoặc phạm vi
module, được nạp vào context của Maia dưới dạng `<instructions source="App|<Module>">`.

Hai điều đáng nhớ:

- Chúng được **đọc một lần lúc bắt đầu phiên và không refresh được**. Sửa xong phải mở phiên
  mới thì Maia mới thấy.
- Agent **không tạo được** chúng trực tiếp — đó là handoff.

Đây là chỗ đặt các ràng buộc kiến trúc của NovaVolt (K10: Mendix chỉ đi qua OData/REST, không
có connection string tới DB .NET; K11: không có đường từ `it-net` sang `ot-net`) sao cho Maia
trong Studio Pro cũng tuân theo, không chỉ agent này. Chưa làm — cân nhắc khi bắt đầu M4.

## Sau khi ghi bằng MCP — bắt buộc

1. `ped_check_errors` một lần, theo error protocol.
2. Nhắc người dùng **Save All** trước khi `mx check` hoặc commit — nếu không, đĩa và model lệch
   nhau và cả hai bên đều tự tin trả lời sai.
3. **Báo cáo bằng ngôn ngữ model, không phải ngôn ngữ tool**: đã tạo/sửa document nào, trong
   module và thư mục nào, vì sao chọn cách đó, và phần nào vẫn cần người dùng tự bấm.
4. Không commit. `AGENTS.md` §1.1.
