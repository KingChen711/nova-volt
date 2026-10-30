# Glossary v2

Thuật ngữ dùng trong code và tài liệu của v2. Tên nhà máy, hệ thống, mã số là hư cấu (ADR-052).
Thêm khái niệm mới vào đây cùng lúc thêm vào model (AGENTS.md §5.8).

## Nền tảng

| Từ | Nghĩa |
|---|---|
| **MOM** | Manufacturing Operations Management: phạm vi quản lý vận hành sản xuất; ở đây là toàn bộ hệ v2 |
| **Foundation** | Lớp cơ chế nền: model, command, transaction, event, OData, rule, key, state machine |
| **Product** | Lớp đóng vai sản phẩm của nhà cung cấp: base app `Production` và các app nền |
| **Project** | Lớp đóng vai team dự án: extension app, FB, app nghiệp vụ; mở rộng Product mà không sửa nó |
| **Solution** | Cấu hình và gói deploy theo plant |
| **Plant** | Phạm vi deploy: một plant một DB MOM; plant nằm trong URL |
| **Domain** | Nhóm dữ liệu của FB: MasterData, OperationalData, ReferenceData |
| **FB (Functional Block)** | Thư viện model và logic của một domain: entity, command, handler, pre-check, post-action, event |
| **App** | Gom nhiều FB, công bố ra ngoài qua POM; viết composite command và reading function |
| **Extension app** | Gắn vào một base app để thêm command, reading function mà giữ nguyên base app; command mang tiền tố tên extension app |
| **POM** | Public Object Model: phần app công bố qua OData (entity, facet, command, reading function) |
| **Command** | Thao tác ghi có tên; đi qua kiểm model, pre-check, handler, post-action, commit |
| **Composite command** | Command của app gọi nhiều command khác; mỗi lời gọi một transaction, không nguyên tử |
| **Pre-check** | Kiểm trước command, chỉ đọc, chặn được; bật theo từng app |
| **Post-action** | Việc thêm sau handler, trong cùng transaction; áp cho mọi app |
| **Facet** | Bộ thuộc tính thêm vào entity có sẵn mà không sửa entity đó |
| **Reading function** | Hàm chỉ đọc của app, trả query trì hoãn; OData lọc tiếp rồi mới đọc DB |
| **Reading model** | Lớp chỉ đọc ánh xạ vào view SQL cùng tên; view cài tay ngoài gói, thiếu thì OData trả 200 với thân bị cắt |
| **Subscription** | Nối một event với subscriber và worker role. *Predefined* khai trong model, bật ở mọi plant; loại còn lại tạo theo plant bằng dữ liệu. Không có subscription thì event vẫn phát nhưng không ai nhận |
| **Worker role** | Nhóm tiến trình chạy command hoặc handler; song song hoặc tuần tự |
| **Signal rule** | Luật gọi một command theo lịch hoặc theo event |
| **Timer** | Bộ đếm giờ tạo lúc chạy, có mã định danh; tới hạn thì các signal rule trỏ vào nó chạy, mỗi nhịp bị lỡ một lần |
| **Relay** | Vòng gửi lại event đã lưu cùng transaction nhưng chưa lên bus (bus chết lúc commit) |
| **Configuration key** | Cặp tên–giá trị cấu hình trong DB (`NId`, `Val`, `Type`, `Category`) |
| **Xóa mềm** | `Delete` đặt `IsDeleted` = số lớn nhất của bảng + 1 thay vì xóa dòng; đọc qua platform tự lọc, SQL trực tiếp thì không |
| **`Query` / `ProjectionQuery`** | Đọc trong transaction của command, chỉ cùng domain / đọc ngoài transaction, mọi domain, chỉ bản đã commit |
| **`Submit`** | Đẩy thay đổi vào transaction để phần sau của chuỗi thấy; commit do nền làm khi chuỗi kết thúc không lỗi |
| **`CallCommand`** | Gọi command khác từ handler: cùng domain thì chung transaction, khác domain thì transaction riêng; người gọi tự kiểm `Succeeded` |
| **Update Database** | Bước căn schema của DB MOM theo model: thêm bảng, cột còn thiếu; tên bảng cắt ngắn kèm hậu tố số, tra ở `Foundation_ModelTable` |
| **Lỗi `6400`** | Service layer từ chối vì thiếu tham số bắt buộc, trước khi handler chạy; dạng `{"error":{"code":...}}`, không có `Succeeded` |
| **CorrelationId** | Mã gắn mọi dòng trace của một lời gọi và các command con của nó; mỗi lời gọi có `CurrentId`, command con có `ParentId` trỏ về cha, cả chuỗi chung `RootRequestId` |

## Sản xuất

| Từ | Nghĩa |
|---|---|
| **Material** | Định nghĩa một loại vật liệu hoặc sản phẩm; có revision hiện hành |
| **MTU** | Material Tracking Unit: một đơn vị cụ thể (một chiếc có serial, hoặc một lô) |
| **MTUA** | Đơn vị gom hoặc chứa MTU và MTUA khác: tote, pallet, trailer kho |
| **BoP / Process / Operation** | Bản thiết kế quy trình: BoP chứa Process, Process gồm các Operation có thứ tự, máy được phép, nguyên liệu định mức |
| **WO / WO Operation** | Lệnh sản xuất: một lần chạy của một Process, và bản chạy của từng Operation |
| **PS (Production Schedule)** | Khung giờ một ca tại một khu, kèm timer hằng ngày |
| **DWO (Default Work Order)** | Mẫu sinh WO: nối một BoP với một PS |
| **Frozen** | Trạng thái WO của ca trước khi hết giờ; thêm vào state machine bằng dữ liệu |
| **Timer đầu ca (`DAILY_WO`)** | Timer của từng PS, chạy mỗi ngày lúc đầu ca: tự Complete WO Frozen đủ giờ, đóng băng WO quá giờ, mở WO mới cho mỗi DWO |
| **`MinimumFreezeHours`** | Thuộc tính lịch của BoP: WO Frozen đủ số giờ này thì tự Complete (24 giờ, Formation 264 giờ) |
| **Suite** | Kịch bản kiểu Tester Tool (`*.suite.json`): test definition, step gọi command hoặc đọc dữ liệu, lặp theo CSV |
| **`#TEST_CSV.<cột>#` / `#GET->…#`** | Tham chiếu trong suite: giá trị của dòng CSV đang chạy / kết quả của step trước |
| **`IfThisSkipNext`** | Step "có kết quả" thì bỏ qua step kế tiếp; mẫu "đọc theo khóa → đã có thì bỏ qua → tạo" |
| **File lịch sử master data** | Danh sách suite đã chạy; deploy bỏ qua suite có tên trong đó, không đếm dữ liệu |
| **Genealogy** | Bản ghi một sản phẩm được làm từ những MTU nào (dòng Assemble) |
| **NC** | Non-conformance: bản ghi "hàng không đạt" gắn với MTU; OPEN, SCRAP, USE AS IS |
| **Cấu hình hủy** | Operation nào nhận mã status nào thì NC xử lý thế nào: kết luận, lượng tách, mã scrap |
| **Containment** | Yêu cầu giữ cả một nhóm hàng nghi lỗi, gửi từ hệ chất lượng ngoài |
| **In Containment** | Cờ trên MTU: MTU đang nằm trong một containment chưa nhả. Close không bỏ cờ, Release mới bỏ |
| **Lịch sử MTU** | Dòng chỉ thêm, không sửa: `Create`, `Assemble` (vào vật chứa, hoặc nguyên liệu trừ cho sản phẩm), `Edit` |
| **`n/a`** | Equipment đặc biệt của hàng chưa có vị trí cụ thể |
| **Buffer** | Chỗ chứa hàng thật đặt tại một equipment; mỗi lô chỉ thuộc một buffer, có thể giới hạn material bằng whitelist |
| **Logistic request** | Yêu cầu chở một MTU tới equipment đích qua các chặng của luồng |
| **EOLV** | Bước đo cuối của thành phẩm họ A, làm sau khi kit đã lên tote và pallet; tin đo gỡ tote chứa kit khỏi pallet |
| **Họ A / Họ B** | Hai họ thành phẩm theo khách: kit hai module / một module mỗi tote |
| **Kit** | Thành phẩm họ A: hai module ghép ở trạm đóng tote, là sản phẩm của WO `Kit_LN<n>` |
| **Tote / pallet** | Vật chứa (MTUA) của thành phẩm: tote chứa một kit hoặc một module; pallet chứa đủ số tote chuẩn của part. Trạng thái theo state machine dạng dữ liệu của team (pallet: Empty, Full, Picked, Staged, Loaded) |
| **Bước đóng gói (`PackingOperation`)** | Bảng cấu hình của team: tin ở mã bước nào thì đóng tote, xếp pallet, hay gỡ tote sau bước đo cuối. Bước không có trong bảng đối chiếu bước chỉ đóng gói, không ghi WO |
| **Số chuẩn (`PalletQty`)** | Property của material: số tote một pallet đủ (họ A 8, họ B 6 ở NV1); cũng là sức chứa của gói lẻ |
| **`BuildDate`** | Ngày đóng pallet, ghi mỗi lần nạp tote; đề xuất lấy hàng theo FIFO xếp theo ngày này, không theo ngày cất kho |
| **Store address** | Ô kho cất thành phẩm của một part, trong bảng địa chỉ part (`PartAddress`) của `MapApp` |
| **Bản ghi FIFO** | Dòng `FifoPallet`: pallet ở đâu, trạng thái, ngày đóng; subscriber ghi sau khi lệnh chuyển pallet commit |
| **Dựng lại pallet** | Màn của xưởng sau bước đo cuối: nạp lại tote đã đo vào một pallet, Completed khi đủ số chuẩn |
| **Kiểm pallet** | Hàng bị giữ hoặc hủy trong cây thành phần (tới 13 tầng), năm điều kiểm số liệu, QA Check (bước kiểm mà lấy hàng dùng) |
| **Gói lẻ (CKD)** | Vật chứa đóng sản phẩm thẳng vào, không qua tote; mã theo tiền tố và độ dài trong configuration key |

## Phía xưởng và hậu cần

| Từ | Nghĩa |
|---|---|
| **LineDB** | Postgres phía xưởng: máy ghi bảng, trigger chép sang bảng trung gian chờ MOM đọc |
| **Relay / poll** | Hai đường chuyển tin từ LineDB vào MOM: nghe notify, hoặc signal rule đọc theo lịch |
| **Trạng thái dòng tin** | `NEW`, `SENT`, `DONE`, `NOTIFY_ERR`, `RETRY`, `FAILED` |
| **Mã lỗi ghép** | Mã lỗi ghi về LineDB: tiền tố theo loại xử lý nối mã gốc |
| **Trailer / lịch xe** | Thùng xe kéo; một chuyến dỡ hoặc chất của nó |
| **Trailer kho** | Trailer đỗ trong nhà máy làm kho tạm: loại thành phẩm (`FGStorage`, theo dõi từng pallet, một part mỗi trailer) hoặc kho chung (`GenericStorage`, khai bằng ghi chú). Trạng thái Free → Empty → Tapped → Full |
| **Complete Load** | Chốt trailer kho đầy: lịch sang chờ gọi đi giao, hoặc chờ đo nếu còn pallet chưa đo |
| **Yêu cầu kéo xe** | Created → Accepted → Picked → Dropped; tới Dropped thì trailer (và pallet trong trailer kho) ở đích |
| **Skid** | Kiện hàng: của nhà cung cấp khi nhận, của đơn bán khi xuất |
| **Placard / yard pass** | Nhãn dán trailer cho biết chở gì / vé cho xe rời nhà máy |
| **Logical dock** | Nhóm thiết bị mang mã dock của nhà cung cấp, gắn vào khu dock thật |
| **Tier / buffer / route** | Bốn cấp vị trí từ dock tới line / ô chứa trong một tier / đường đi qua các tier |
| **Kanban / địa chỉ part** | Hồ sơ cung ứng của part / chỗ cất và chỗ giao của part |

## Xuất hàng

| Từ | Nghĩa |
|---|---|
| **Đơn bán / nhóm đơn** | Đơn của khách từ ERP / các đơn cùng nơi nhận, route, giờ giao chưa giao, đi chung một chuyến |
| **Chuyến giao (delivery)** | Một lần giao của nhóm đơn: xe, số xác nhận của khách, vận đơn, yard pass |
| **Họ A / họ B** | Họ khách: A có hãng vận tải trong đơn, nhãn SkidTag, đo cuối, ASN; B dùng dữ liệu đặt hàng, nhãn SkidSheet hai mặt, nhãn hộp, chờ `CustomerShipSys` xác nhận |
| **Picking / staging** | Giữ pallet cho đơn, vị trí giữ ô kho / xác nhận pallet đã tới khu tập kết, gắn vào skid, đổi vị trí |
| **Route chính** | Tên nhóm equipment của route: route từ 10 ký tự lấy 3 ký tự đầu, ngắn hơn bỏ 2 ký tự cuối; chỉ ra khu tập kết và ô yard |
| **Ngoại lệ chất xe** | Mã giải thích vì sao chuyến lệch kế hoạch, theo nhóm SkidLevel, SkidBuild, Trailer; một mã do MOM tự khai |
| **Vận đơn / ASN** | Chứng từ in khi chất xong / tin báo khách họ A chuyến vừa đi |
| **Route pool** | Nơi nhận mà một xe của hãng vận tải phục vụ |

## Hệ bên ngoài (giả lập)

| Tên | Vai |
|---|---|
| `ERP` | Đơn bán vào; sản lượng, tiêu hao, scrap ra |
| `SupplierPortal` | Lịch giao linh kiện vào; địa chỉ part ra |
| `CustomerShipSys` | Xác nhận chuyến xe của họ B |
| `CustomerBoxOrder` | Dữ liệu đơn và nhãn hộp của khách họ B |
| `QualityTrace` | Gửi containment và lệnh nhả hàng |
| `WMS` | Kho đầu chuỗi gọi hàng, nhận picklist |
| `ReorderSvc` | Nhận danh sách đặt bù hàng scrap |
