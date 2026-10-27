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
| **Reading function** | Hàm chỉ đọc của app, trả danh sách qua OData |
| **Subscription** | Nối một event với handler và worker role; không có thì event không ai nhận |
| **Worker role** | Nhóm tiến trình chạy command hoặc handler; song song hoặc tuần tự |
| **Signal rule** | Luật gọi một command theo lịch hoặc theo event |
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
| **Genealogy** | Bản ghi một sản phẩm được làm từ những MTU nào (dòng Assemble) |
| **NC** | Non-conformance: bản ghi "hàng không đạt" gắn với MTU; OPEN, SCRAP, USE AS IS |
| **Cấu hình hủy** | Operation nào nhận mã status nào thì NC xử lý thế nào: kết luận, lượng tách, mã scrap |
| **Containment** | Yêu cầu giữ cả một nhóm hàng nghi lỗi, gửi từ hệ chất lượng ngoài |
| **EOLV** | Bước đo cuối của thành phẩm họ A, làm sau khi kit đã lên tote và pallet |
| **Họ A / Họ B** | Hai họ thành phẩm theo khách: kit hai module / một module mỗi tote |

## Phía xưởng và hậu cần

| Từ | Nghĩa |
|---|---|
| **LineDB** | Postgres phía xưởng: máy ghi bảng, trigger chép sang bảng trung gian chờ MOM đọc |
| **Relay / poll** | Hai đường chuyển tin từ LineDB vào MOM: nghe notify, hoặc signal rule đọc theo lịch |
| **Trạng thái dòng tin** | `NEW`, `SENT`, `DONE`, `NOTIFY_ERR`, `RETRY`, `FAILED` |
| **Mã lỗi ghép** | Mã lỗi ghi về LineDB: tiền tố theo loại xử lý nối mã gốc |
| **Trailer / lịch xe** | Thùng xe kéo; một chuyến dỡ hoặc chất của nó |
| **Skid** | Kiện hàng: của nhà cung cấp khi nhận, của đơn bán khi xuất |
| **Placard / yard pass** | Nhãn dán trailer cho biết chở gì / vé cho xe rời nhà máy |
| **Logical dock** | Nhóm thiết bị mang mã dock của nhà cung cấp, gắn vào khu dock thật |
| **Tier / buffer / route** | Bốn cấp vị trí từ dock tới line / ô chứa trong một tier / đường đi qua các tier |
| **Kanban / địa chỉ part** | Hồ sơ cung ứng của part / chỗ cất và chỗ giao của part |

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
