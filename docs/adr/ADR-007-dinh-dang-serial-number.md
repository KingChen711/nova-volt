# ADR-007 — Giữ serial 16 ký tự và kiểm format tại backend

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-07 |
| **Liên quan** | [scope.md](../scope.md) §6.1 · [plan M4](../plans/M4-mendix-operator-station.md) C01 · AGENTS K3/K4/K5 |

## Context

Operator Station cần nhận serial từ scanner, tìm đúng sản phẩm và phân biệt mã sai format với mã
đúng nhưng không tìm thấy trong site được phép. Kernel đã có `SerialNumber` và test từ M1;
M4 dùng lại contract này. ADR ghi quyết định hiện có, không giới thiệu bộ cấp số mới.

Scope yêu cầu mã đọc được trên sản phẩm; quy mô dùng để tính sức chứa là 6.000 cell/ngày,
ba ca và hai line. Chưa có serial allocator hay luồng xử lý trùng mã của M5.

## Decision

Giữ layout **16 ký tự ASCII viết hoa/số**, không có dấu phân cách:

| Thành phần | Độ dài | Giá trị |
|---|---|---|
| Site | 3 | Mã site; sự tồn tại và quyền truy cập được kiểm riêng |
| Loại unit | 1 | `C`, `M`, `P` |
| Line | 2 | Một chữ cái và một chữ số, ví dụ `L1`, `P1` |
| Năm | 1 | Chữ số cuối của năm |
| Ngày trong năm | 3 | `001`–`366` |
| Ca | 1 | `A`, `B`, `C` |
| Số thứ tự | 5 | `00001`–`99999` |

Ví dụ pack M4: `NV1PP16250A00001` = `NV1 / P / P1 / 6 / 250 / A / 00001`.
`SerialNumber.TryParse` là validator format phía .NET. Không tự uppercase, sửa ký tự hoặc thêm số
cho scanner. Enter của scanner là thao tác submit, không phải một ký tự của serial được lưu.

Format hợp lệ không chứng minh sản phẩm tồn tại, thuộc site của user hay được phép thao tác.
Backend kiểm các điều đó riêng; site in trên mã không thay principal đã xác thực. Lỗi format trả
thông báo tiếng Việt; mã không tồn tại và mã ngoài site không để lộ khác biệt về sự tồn tại.

M4 chỉ đọc serial đã có trong fixture. Cấp số, unique constraint và xử lý `DuplicateSerialDetected`
vẫn ở M5 theo scope; không đổi mã lịch sử để tránh collision.

## Consequences

**Được**

- Mendix, API và fixture dùng cùng layout; không có regex UI tự diễn giải khác kernel.
- 99.999 số/ca/line so với `6.000 / 3 / 2 = 1.000` unit/ca/line: sức chứa xấp xỉ 100 lần tải mẫu.

**Mất / phải chịu**

- Parser nghiêm ngặt làm scanner cấu hình sai bị từ chối; UI phải báo lỗi dễ hiểu thay vì âm thầm sửa mã.
- Một chữ số năm lặp lại sau mười năm. Sức chứa mỗi ca **không chứng minh uniqueness trong 15 năm**;
  parser cũng không quyết được năm đầy đủ hay ngày 366 có hợp lệ với năm sản xuất cụ thể hay không.
- Bản thân mã khắc có thể trùng do lỗi vật lý. Luồng cấp số M5 phải xử lý collision như scope đã yêu cầu;
  việc parse thành công không cho phép bỏ qua kiểm tra đó hoặc ghi đè hồ sơ cũ.

**Việc phát sinh:** C03 dùng helper hiện có kiểm fixture; C04 kiểm riêng sai format/không tìm thấy;
M5 kiểm uniqueness và ngoại lệ trùng mã. ADR này không tuyên bố các phần M5 đã được thực hiện.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Đổi mã hiển thị thành GUID | Đổi contract đã dùng trong kernel, telemetry và tài liệu; không giải quyết việc người dùng quét nhầm mã |
| Tự sửa chữ thường hoặc mã thiếu ký tự trong UI | Che lỗi scanner và có thể tra sang một mã khác với thứ thực sự đọc được |
| Viết lại allocator/format với năm đầy đủ ở M4 | Mở rộng phạm vi ngoài scan; cần một quyết định migration riêng, không làm như thay đổi UI |

## Evidence

- Đã đọc [SerialNumber.cs](../../src/Platform/Nvm.Kernel/Identity/SerialNumber.cs) và
  [SerialNumberTests.cs](../../tests/Unit/Nvm.UnitTests/Identity/SerialNumberTests.cs) tại `be05e43`.
- Chạy ngày 2026-09-07:

  ```powershell
  dotnet test --project tests/Unit/Nvm.UnitTests/Nvm.UnitTests.csproj --no-restore -- --filter-class Nvm.UnitTests.Identity.SerialNumberTests
  ```

  Kết quả **22 passed, 0 failed, 0 skipped**. Chỉ kiểm parser hiện có, chưa kiểm UI/POM của M4.
- Sức chứa ở trên là phép tính từ số trong scope, không phải benchmark máy khắc hay throughput runtime.
