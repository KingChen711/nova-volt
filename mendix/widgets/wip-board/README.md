# NovaVolt WIP board

Widget riêng cho `NvmShared.WipBoard` trong NvmShopFloor, Studio Pro11.12.3. Đọc qua
Mendix external entity/POM với quyền của session hiện tại; không giữ token backend
hay truy cập DB. Bốn cột: Line, StepCode, QualityState, UnitCount.

```powershell
npm ci --ignore-scripts
npm test
npm run build
```

Gói tạo tại `dist/1.0.0/novavolt.WipBoard.mpk`. Chép vào thư mục `widgets` của app,
Synchronize App Directory trong Studio Pro, rồi thêm **NovaVolt WIP board** lên page
đã cấp quyền. Build gói không chứng minh app chạy; cần kiểm model, F5 và browser.

Mỗi trang đọc tối đa50nhóm theo Line/StepCode/QualityState. Trang đủ50nhóm cho phép
sang trang tiếp, có thể gặp trang rỗng khi tổng số nhóm là bội số50. Không tải tất cả
nhóm vào bộ nhớ. Pagination trực tiếp trên dữ liệu đang thay đổi không phải snapshot
xuyên nhiều trang.

Timer5giây bắt đầu sau khi request kết thúc; refresh tay và phân trang dùng chung
khóa request. Khi lỗi, giữ nguyên bản sao giá trị, trang và thời điểm thành công cũ,
hiện Disconnected rồi thử lại. Kết quả rỗng thành công khác với lần đọc đầu thất bại.
Rời page hủy timer và bỏ kết quả muộn; API không có AbortSignal nên không tuyên bố
request đang chạy bị hủy phía server. Thời gian hiển thị là lúc browser nhận thành
công, theo đồng hồ máy người dùng, không phải thời gian projection cập nhật.

Nguồn API: [Mendix11 retrieveByEntity](https://apidocs.rnd.mendix.com/11/client-mx-api/module-mx-api_data.html#retrieveByEntity).
SDK resolve/reject của chính request quyết định trạng thái; không suy từ
`ListValue.status` hoặc gọi healthcheck riêng. Npm mendix11.8 thiếu declaration của
`mx-api/data`, nên `src/mx-api.d.ts` khai báo phần API dùng theo tài liệu. Implementation
được Studio Pro cung cấp qua import external, không đóng gói client Mendix riêng.
Probe runtime11.12.3 đã đọc được WipBoard NV1 qua cùng implementation trước khi dựng
widget; widget vẫn cần kiểm đầy đủ sau khi gắn vào app.

Unit tests dùng clock và promise điều khiển để kiểm overlap, lỗi/recovery, pagination
và unmount. Chúng không thay runtime test mất kết nối, quyền hai site hoặc latency.
