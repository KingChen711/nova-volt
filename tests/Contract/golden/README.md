# Golden file

Mỗi file ở đây là **một bản JSON thật** của một event, ở một schema version.

Test đọc chúng và assert rằng code **hôm nay** vẫn hiểu được. Đó là bằng chứng duy nhất cho lời hứa
mà `scope.md` §8.4 đặt ra: *code phiên bản 2036 phải đọc được event ghi bởi code 2026*. Event store
giữ **15 năm** và là hồ sơ pháp lý — không `UPDATE`, không `DELETE`.

## Luật

1. **Viết tay từ contract, không sinh ra bằng `Serialize()`.** File sinh từ chính code cần kiểm là
   một vòng lặp tự khẳng định: code đổi thì file đổi theo, và test mãi mãi xanh.
2. **Không bao giờ sửa file của version đã phát hành.** Version mới → **file mới**. Sửa file cũ là
   xoá đúng thứ đáng giá nhất: bằng chứng về hình dạng dữ liệu đang nằm trong store.
3. **Được sửa khi version đó chưa từng chạy thật.** Bản nháp trong lúc phát triển thì còn sửa được;
   ranh giới là lần đầu event được publish ra ngoài process, không phải lúc tạo file.
4. Một file = một version. Đặt tên `<event>.v<n>.json`, xếp theo bounded context.

## Danh sách

| Context | Event | Version | Ra ở | Trạng thái |
|---|---|---|---|---|
| `factory-model` | `revision-activated` | v1 | M1 · C03 | **nháp** — chưa publish thật, còn sửa được tới hết M1 |
