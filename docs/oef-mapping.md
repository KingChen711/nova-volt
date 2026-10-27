# Bảng cơ chế nền v2

Mỗi dòng là một cơ chế của nền kiểu Opcenter EX mà Foundation phải tái hiện theo hành vi đã quan sát
([scope §3.1](scope.md#31-cơ-chế-foundation-phải-tái-hiện)). Cột **Test** ghi tên test chứng minh
hành vi; chưa có test thì chưa tính là xong.

| Trạng thái | Nghĩa |
|---|---|
| `chưa làm` | Chưa có code |
| `đang làm` | Có code, thiếu test cho ít nhất một hành vi |
| `xong` | Mọi hành vi trong dòng có test xanh |

| Cơ chế | Mốc | Trạng thái | Test |
|---|---|---|---|
| Domain, `Query`/`ProjectionQuery`, analyzer chặn dùng nhầm model | R1 | `chưa làm` | |
| Cột hệ thống, khóa logic, ràng buộc bắt buộc ở tầng model | R1 | `chưa làm` | |
| Xóa mềm bằng số đếm `IsDeleted` | R1 | `chưa làm` | |
| Facet trên entity của lớp Product | R1 | `chưa làm` | |
| Kiểm model ở service layer (lỗi `6400`) | R1 | `chưa làm` | |
| Pre-check theo app | R1 | `chưa làm` | |
| Post-action trong cùng transaction, áp mọi app | R1 | `chưa làm` | |
| Transaction: chuỗi trong FB và composite không nguyên tử | R1 | `chưa làm` | |
| Dạng lỗi trả về (handler, service layer) | R1 | `chưa làm` | |
| Trace theo `CorrelationId` | R1 | `chưa làm` | |
| Event phát sau commit | R2 | `chưa làm` | |
| Subscription, worker role | R2 | `chưa làm` | |
| Reading function, reading model là view | R2 | `chưa làm` | |
| App, POM, OData theo app, token có hạn | R2 | `chưa làm` | |
| Extension app: tiền tố, phẳng | R2 | `chưa làm` | |
| Signal rule, timer | R2 | `chưa làm` | |
| Configuration key | R2 | `chưa làm` | |
| State machine dạng dữ liệu, role theo transition | R2 | `chưa làm` | |
| Gửi tin ra ngoài đồng bộ và bất đồng bộ | R11 | `chưa làm` | |
| Gói deploy, cập nhật database, master data theo thứ tự | R12 | `chưa làm` | |
