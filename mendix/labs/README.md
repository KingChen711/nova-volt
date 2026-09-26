# Kiểm WIP trên runtime thật

Yêu cầu Node.js ≥20 hỗ trợ `fetch`, Chrome đã cài, Mendix tại `localhost:8080`, Keycloak
tại `localhost:8081` và Execution/POM tại `localhost:5081`. App phải có menu **WIP**,
quyền Operator, NovaVolt WIP board bốn cột và refresh 5 giây. Fixture mỗi site cần có nhóm
EOL và từ 1 đến 50 nhóm; không thay đổi fixture trong lúc đo.

Từ thư mục này:

```powershell
npm ci --ignore-scripts
npm run wip
npm run wip:slow
npm run wip:failure
```

Lab gây outage thật (chỉ chạy trên môi trường local này, dừng tạm Execution dùng chung):

```powershell
npm run wip:backend-down -- --capture
```

Lệnh yêu cầu `nvm-execution` đang chạy, dừng container, kiểm UI lỗi, rồi start và đợi
health/ready trước thử lại. Nhánh finally cũng cố phục hồi backend khi assertion lỗi.
Không chạy đồng thời lab khác dùng Execution. `--capture` lưu ảnh trong
`output/playwright/wip` (gitignored); không lưu cookie/token.

Lab dùng hai tài khoản local `op.nv1`/`op.de1` từ Keycloak seed hiện có; credential và
token chỉ nằm trong bộ nhớ. Không gửi command nghiệp vụ. Mỗi lượt mở browser context
riêng, đăng nhập SSO, gọi logout Mendix và đợi HTTP200 trước khi đóng context;
cleanup cũng chạy khi assertion lỗi. Đóng tab đơn thuần không hủy session server và
có thể làm các lượt sau chạm giới hạn session của trial license. Không xuất trace chứa credential.

- `wip`: đối chiếu từng nhóm/cột/count của browser với POM đã xác thực cho đúng site;
  quan sát ít nhất ba request tự tải hoàn tất cho mỗi tài khoản.
- `wip:slow`: trên NV1, browser giữ mỗi phản hồi `/xas/` có action `retrieve_by_xpath` và XPath chính xác `//NvmShared.WipBoard`
  thêm 7,5 giây. Quan sát ít nhất hai lượt hoàn tất và không có request chồng nhau.
  Không thay delay backend, proxy dùng chung hoặc cấu hình app.
- `wip:failure`: chặn riêng request WIP trong browser, kiểm Disconnected và giữ nguyên
  dữ liệu/thời điểm cũ; bỏ chặn và bấm Retry, kiểm Connected và thời điểm mới.
- Lượt baseline/slow kiểm HTTP 200, không lỗi transport, không còn request đang chạy và POM giữ
  nguyên dữ liệu đầu/cuối phép đo. Thời gian đo dùng đồng hồ đơn điệu của Node.js.

Output JSON chỉ gồm tài khoản, dữ liệu WIP và thời gian/status của request; không
gồm URL callback, header, cookie, token hoặc body request. Exit code khác 0 nghĩa là
phép kiểm chưa đạt. Timeout 45 giây là giới hạn quan sát, không phải SLO sản phẩm.

Giới hạn: đây là kiểm chức năng và điều phối refresh trên fixture nhỏ, không thay
phép đo p95 trang <1,5 giây, tải sản xuất, sự cố backend thật, hay kiểm nội dung lỗi ứng dụng
trong response HTTP 200. Delay sau khi backend đã xử lý không chứng minh thời gian
truy vấn backend; nó chỉ kiểm browser có đợi phản hồi trước khi tải tiếp hay không.
Thay đổi UI/datasource phải cập nhật lại cách nhận diện request và chạy lại lab.
