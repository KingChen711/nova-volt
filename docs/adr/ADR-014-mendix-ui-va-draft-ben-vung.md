# ADR-014 — Mendix sở hữu UI và draft, backend sở hữu quyết định nghiệp vụ

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-07 |
| **Liên quan** | [scope.md](../scope.md) §7.5/§9/M4 · [plan M4](../plans/M4-mendix-operator-station.md) C01/C07 · [ADR-038](ADR-038-data-collection-thu-cong-o-m4.md) · AGENTS K3/K10 |

## Context

M4 đưa người vận hành vào vòng auth → query → command. App NvmShopFloor và OIDC đã có từ M0.
Lab yêu cầu UI báo **"hệ thống tạm thời không phản hồi, dữ liệu đã được ghi tạm"** khi Execution ngừng
hoạt động. Câu đó chỉ đúng nếu nội dung đã được lưu bền vững ở nơi còn hoạt động khi backend chết.

Mendix `Commit object` chưa nhất thiết kết thúc transaction: database COMMIT xảy ra khi microflow
và các microflow gọi nó hoàn tất. Vì vậy commit object rồi gọi REST trong cùng một chuỗi microflow
có rollback chưa đủ bảo vệ draft. [Tài liệu Mendix](https://docs.mendix.com/refguide/committing-objects/).

## Decision

Mendix là lớp UI duy nhất theo scope. Dùng app hiện có; `NvmShared` chứa account, OIDC, connector
và response mapper. Module `NvmShopFloor` sở hữu bốn màn hình M4 và `DataCollectionDraft`.
POM cung cấp dữ liệu đọc; Command API kiểm quyền và điều kiện nghiệp vụ. Mendix không nối DB của .NET.

**Quyền M4**, áp dụng cho cả page/microflow/entity và kiểm độc lập ở backend:

| Thao tác | Operator | LineLeader |
|---|---|---|
| Dispatch, scan, WIP | Trong site của mình | Trong site của mình |
| Nhập/gửi kết quả đo | Trong context hợp lệ tại site mình | Như Operator |
| Đọc/sửa draft chưa gửi, retry draft chờ xác nhận | Chỉ draft của chính mình | Chỉ draft của chính mình |
| Override, release, hoàn tất công đoạn | Không có quyền/chức năng M4 | Không có quyền/chức năng M4 |

Principal phía backend là nguồn quyền/site/actor. XPath theo site và owner bảo vệ draft trong Mendix;
không dùng filter UI thay kiểm ở POM. Token ở cơ chế OIDC phía server, không chép vào draft, URL hay log.
Entry page cung cấp login rõ ràng; app không tự tạo lại cấu hình OIDC/runtime data từ M0.

Draft chứa owner, site, submission ID, thời điểm xác nhận nhập, key, payload và outcome khi nhận được.
Bốn trạng thái đủ cho M4:

| Trạng thái | Ý nghĩa / hành vi |
|---|---|
| `Editing` | Chưa gửi; owner được sửa hoặc bỏ draft |
| `Pending` | Nội dung đã đóng băng và lưu trước POST; chưa có câu trả lời chắc chắn từ backend. Retry dùng đúng request cũ |
| `Accepted` | Backend xác nhận đã nhận. Giữ outcome; không cho sửa thành một kết quả khác |
| `Rejected` | Backend trả từ chối nghiệp vụ rõ ràng. Hiện lý do; muốn thực hiện một ý định mới thì tạo draft mới tường minh |

Request lưu/chuyển `Pending` phải **kết thúc transaction trước** request gọi POST. Phía server kiểm
owner/site và quyền sửa theo trạng thái; chỉ khoá input ở browser không đủ. `occurredAt` được lưu
lúc người dùng xác nhận nhập và giữ nguyên khi retry; không mô tả nó như timestamp từ thiết bị.

Khi Execution không phản hồi, draft đã lưu mới được hiện câu lab nguyên văn. Nếu lưu draft thất bại,
hiện không lưu được. Nếu POST timeout, giữ `Pending`: backend có thể đã commit; không cấp key mới
hay báo chắc chắn chưa nhận. Có thể hiện thêm "Chưa xác nhận được backend đã nhận; gửi lại để kiểm tra."

Gửi lại là thao tác của người dùng. Không có background sync, offline app, hàng đợi relay tự động hay
cơ chế chia sẻ draft cho LineLeader. Nếu backend trả 401/403, giữ nội dung và yêu cầu khôi phục quyền/login;
không biến lỗi quyền thành thông báo backend ngừng hoạt động.

## Consequences

**Được**

- Câu "đã ghi tạm" có bằng chứng kiểm được bằng reload/restart Mendix giữ DB.
- Quy tắc sản xuất có một nơi thực thi; UI nhận lý do và hành động từ backend theo contract chung.

**Mất / phải chịu**

- Có thêm một persistent entity và hai ranh giới request; cần kiểm cả lỗi lưu draft lẫn lỗi gửi command.
- Draft chờ xác nhận có thể tồn tại dù SQL Server đã nhận kết quả. Người vận hành phải retry để đồng bộ
  trạng thái; M4 không bảo đảm hai DB đổi trạng thái tức thì hoặc tự khôi phục không cần thao tác.
- Người dùng không thể sửa nội dung đã gửi mà giữ cùng key. Sửa nháp mới không sửa được bản ghi nghiệp vụ
  đã Accepted; correction theo K5 cần luồng riêng, không được trá hình bằng edit draft.
- Khi chính Mendix server/DB ngừng hoạt động, cơ chế này không nhận thêm dữ liệu offline ở browser.
  Không tuyên bố Operator Station M4 đã sẵn sàng cho mọi tình huống mất mạng của nhà máy thật.

**Việc phát sinh:** C07 kiểm save rồi reload trước POST, timeout và access theo owner/site;
C09 chạy lab trong scope. Lưu outcome/retry theo ADR-038; không xây outbox sớm trong Mendix.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Toast "đã ghi tạm" với object chỉ ở RAM/browser | Reload hoặc process chết làm mất nội dung, trái lời hứa của lab |
| Gửi REST rồi mới lưu draft | Mất backend đúng lúc gửi khiến chưa có bản bền vững để người dùng khôi phục |
| Dùng DB .NET làm nơi lưu draft trực tiếp | Vi phạm K10; backend ngừng hoạt động cũng không còn đường lưu độc lập |
| Offline-first app và tự đồng bộ | Tăng phạm vi thành giải bài toán nhiều bản sao/conflict; lab M4 chỉ cần backend outage và retry thủ công |

## Evidence

- Scope §9/M4 giữ nguyên tám DoD và câu lab. C01 chốt quyền và nơi sở hữu dữ liệu theo yêu cầu triển khai
  plan của owner ngày 2026-09-07; đây là quyết định thiết kế, chưa phải kết quả nghiệm thu UI.
- Quy tắc transaction dựa trên tài liệu Mendix dẫn ở Context. Chưa chạy fault/reload trên model M4;
  C07/C09 phải cung cấp bằng chứng runtime trước khi đóng D3–D5/lab.
