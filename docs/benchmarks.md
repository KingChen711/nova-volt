# Sổ ghi số đo

Mọi con số trong repo này phải có một dòng ở đây. Không có dòng thì không được nhắc tới con số
đó ở chỗ khác.

## Luật ghi

1. **Chỉ số thật.** Không ước lượng, không "khoảng", không "tầm". Chưa đo thì để trống, đừng đoán.
2. **Điều kiện đo là bắt buộc.** Một con số không kèm điều kiện là một con số vô nghĩa —
   `make up` 48 s với image đã cache và 48 s khi phải tải image là hai chuyện khác hẳn.
3. **Một phép đo = một dòng.** Đo ba lần thì ba dòng, đừng lấy trung bình rồi ghi một dòng. Độ
   phân tán là thông tin.
4. **Đo lại thì thêm dòng mới**, không sửa dòng cũ. Số cũ cho biết thay đổi nào làm chậm đi.
5. Cột `Commit` là commit **tại thời điểm đo**, không phải commit thêm dòng này.

Máy đo: Windows 11, Docker Desktop, quota RAM 8 GB (xem `docs/plans/M0-bootstrap.md` §C09.4).

---

## M0 — Bootstrap

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-25 | `3aebc86` | Khởi động toàn bộ stack từ `down` | **58 s** | 6 service 53 s + profile `init` 5 s. Image **đã có sẵn trong cache** |
| 2026-08-25 | `3aebc86` | Tổng `mem_limit` khai báo | **6,00 GB** | 6 service của profile mặc định. Quota Docker 8 GB |
| 2026-08-25 | `3aebc86` | RAM thực dùng lúc rảnh | **~2,1 GB** | `docker stats`, không tải, không ai đăng nhập |
| 2026-08-26 | `4f6408f` | 5 readiness probe lúc bình thường | **451 ms** | Tổng thời gian cả 5 check trong một lần gọi `/health/ready` |
| 2026-08-26 | `4f6408f` | Phát hiện SQL Server chết | **3,2 s** | Tắt container `mssql` → `/health/ready` chuyển `Unhealthy`. Ngưỡng D5: < 10 s |
| 2026-08-26 | `4f6408f` | Phục hồi sau khi bật lại SQL Server | **13 s** | `Application started` chỉ xuất hiện **1 lần** → app không restart |
| 2026-08-26 | `4f6408f` | Khởi động app khi SQL Server đang tắt | **2 s** | `live=Healthy`, `ready=Unhealthy[sqlserver]`. Chứng minh ràng buộc N15 |
| 2026-08-26 | `0719cad` | `make up` lần 1 | **48 s** | Sau `make down-v` — volume rỗng, image đã cache |
| 2026-08-26 | `0719cad` | `make up` lần 2 | **36 s** | Sau `make down` — giữ volume |
| 2026-08-26 | `0719cad` | `make up` lần 3 | **42 s** | Sau `make down` — giữ volume |
| 2026-08-26 | `0719cad` | Kích thước `git bundle` backup | **153 KB** | `make backup`, `git bundle verify` trả OK |
| 2026-08-26 | `f6f8fb8` | 5 readiness probe, lời gọi **lặp lại** | **10,4 ms** rồi **21,4 ms** | Cùng endpoint như dòng 451 ms ở trên, nhưng container đã ấm và connection pool đã mở. Chênh ~40 lần → 451 ms là **giá của lần đầu**, không phải giá thường trực |

> [!note] Ba con số `make up` đo cái gì, và không đo cái gì
> 48/36/42 s là thời gian **khởi động** với image đã nằm trong cache Docker — thoải mái so với
> ngưỡng 300 s của ★D1. Chúng không bao gồm thời gian **tải image** (`mssql` một mình hơn 1 GB).
>
> ★D1 đã được phát biểu lại cho khớp: *"sau `make down-v`, image đã cache"*. Thời gian tải phụ
> thuộc đường truyền chứ không phụ thuộc repo, nên đo nó không nói lên điều gì về code. Khi nào
> có người thứ hai clone repo thì thêm một dòng với điều kiện *"máy chưa từng có image"*.

---

## M1 — Factory Model & Manufacturing Service Bus

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-27 | `0094509`+C13 | ★ **Event mất khi broker chết 30 s** | **18 / 200** | `make bus-chaos`. Publish 200 event cách nhau 100 ms, timeout 2 s mỗi lần; `stop rabbitmq` ở giây thứ 5, `start` sau 30 s. **Chưa có outbox** — xem `ADR-022` |
| 2026-08-27 | `0094509`+C13 | Cửa sổ mất, theo số thứ tự event | **48 → 65** | Cùng lần chạy. Một khối liền, không rải rác |
| 2026-08-27 | `0094509`+C13 | Publish thành công / consumer nhận được | **182 / 182** | Cùng lần chạy. Hai số **bằng nhau**: số mất đúng bằng số publish thất bại, không có vùng xám |
| 2026-08-27 | `0094509`+C13 | Thời gian chạy hết 200 event | **59,0 s** | Cùng lần chạy. Chạy trơn mất ~20 s; 39 s chênh là 18 lần × 2 s timeout |
| 2026-08-27 | `0094509`+C13 | Số lần app restart trong lúc broker chết | **0** | `Application started` xuất hiện đúng 1 lần trong `host.log`. `/health/live` = `Healthy` suốt |
| 2026-08-27 | `0094509`+C13 | Số check `ready` đỏ khi RabbitMQ tắt | **1 / 6** | Chỉ check `rabbitmq`. `sqlserver`, `postgres`, `keycloak`, `minio`, `masstransit-bus` vẫn xanh — lỗi không lan |
| 2026-08-27 | `0094509`+C13 | Số lần thử của consumer lỗi | **5** | `make bus-dlq`. Khoảng cách đo được: 245 / 480 / 920 / 1933 ms — exponential có jitter, khớp `NvmRetryPolicy` |
| 2026-08-27 | `0094509`+C13 | Message trong `_error` sau 5 lần thử | **1** | Cùng lần chạy. Queue chính còn **0** — không mất, chỉ đứng riêng |
| 2026-08-27 | `4e7fc02`+C14 | **6** readiness probe, lời gọi đầu | **272,3 ms** | Cả 6 `Healthy`. M0 đo **451 ms** với 5 probe — thêm probe thứ sáu không làm chậm đi |
| 2026-08-27 | `4e7fc02`+C14 | 6 readiness probe, lời gọi lặp lại | **7,9 ms** rồi **7,0 ms** | Cùng endpoint, container đã ấm. M0: 10,4 / 21,4 ms với 5 probe |
| 2026-08-27 | `4e7fc02`+C14 | ★ **Khởi động app khi RabbitMQ đang tắt** | **249 ms** | App lên bình thường. `live=Healthy`; `ready=Unhealthy` ở **`bus`** (*"Not ready: not started"*, *"Broker unreachable"*) và **`rabbitmq`**; 4 probe còn lại xanh |
| 2026-08-27 | `4e7fc02`+C14 | Phát hiện SQL Server chết | **3157 ms** | Regression của D5/M0 (đo 3,2 s). Ngưỡng < 10 s. Chỉ `sqlserver` đỏ, 5 probe khác không lan |
| 2026-08-27 | `4e7fc02`+C14 | ★ `bus` phát hiện broker chết **sau** khi bus đã khởi động | **không phát hiện** | `Healthy` liên tục **152 s** với broker đã `docker compose stop`. Xem ghi chú bên dưới |
| 2026-08-27 | `4e7fc02`+C14 | `ready` xanh lại sau khi bật lại broker — lần 1 | **19,8 s** | Broker tắt ~40 s trước đó. Con số này chủ yếu là thời gian **container RabbitMQ khởi động**, không phải thời gian app nối lại |
| 2026-08-27 | `4e7fc02`+C14 | `ready` xanh lại sau khi bật lại broker — lần 2 | **4,7 s** | Broker tắt ~10 s. `check_running` của broker và `ready` của app xanh trong **cùng một nhịp poll 1 s** → phần app tự đóng góp < 1 s |
| 2026-08-27 | `4e7fc02`+C14 | Số lần app restart trong cả bốn kịch bản trên | **0** | `Application started` đúng 1 lần mỗi lần chạy |

> [!warning] `bus` và `rabbitmq` không thay thế được cho nhau — và đây là lý do
> Hai probe bắt hai loại hỏng **khác nhau**, và mỗi cái mù với loại kia:
>
> | | broker chết **trước** khi app khởi động | broker chết **sau** khi bus đã chạy |
> |---|---|---|
> | `bus` | ✅ bắt được (25 ms) | ❌ **Healthy suốt 152 s** |
> | `rabbitmq` | ✅ bắt được | ✅ bắt được |
>
> `bus` là health check của MassTransit: nó nói về **bus trong process này** — đã khởi động chưa, các
> receive endpoint sẵn sàng chưa. `Nvm.Host.All` chỉ publish nên **không có receive endpoint nào**, và
> một bus đã khởi động xong thì không còn gì để báo hỏng. Xoá probe `rabbitmq` vì "MassTransit đã có
> health check rồi" là để lại đúng kịch bản nguy hiểm nhất — broker chết giữa ca — không ai canh.
>
> Ngược lại cũng không xoá được `bus`: nó là thứ giữ `ready` đỏ trong lúc bus đang khởi động, trước
> khi có connection nào để `rabbitmq` kiểm.

> [!important] Con số 18 nói lên cái gì, và không nói lên cái gì
> Nó **không** phải chỉ số chất lượng của RabbitMQ hay của MassTransit. Cả hai làm đúng việc của
> mình: broker chết thì không nhận được gì, và client báo lỗi thay vì nuốt.
>
> Nó là giá của việc **ghi hai nơi mà không có transaction nào nối chúng lại** — bẫy dual-write ở
> `scope.md` §5.5. Con số này tồn tại để trả lời câu hỏi mà M6 sẽ bị hỏi: *outbox thêm một bảng, một
> worker và một transaction, đổi lại được gì?* Đổi lại 18 event, trong 30 giây, ở một hệ thống chưa
> có tải.
>
> Cột `Commit` ghi `0094509`+C13 vì phép đo chạy trên cây làm việc của C13 trước khi commit. Thay
> bằng hash thật của C13 ngay sau khi commit.

---

## Mục tiêu SLO — còn phải đo

Khung lấy từ `docs/scope.md` Phụ lục A. Điền khi tới milestone tương ứng; mỗi ô điền xong phải
có một dòng chi tiết ở bảng phía trên.

| Chỉ số | Mục tiêu | M2 | M6 | M9 | M13 |
|---|---|---|---|---|---|
| Ingestion throughput (msg/s) | ≥ 5.000 | | | | |
| Ingestion lag p95 (s) | < 5 | | | | |
| Forward trace p95 (ms) | < 200 | — | | | |
| Backward trace p95 (ms) | < 150 | — | | | |
| Command API p95 (ms) | < 300 | — | | | |
| Projection lag p95 (s) | < 3 | — | | | |
| Rebuild toàn bộ (phút) | < 10 | — | | | |
| Matching 100k cell (s) | < 30 | — | — | | |
| CP-SAT vs greedy (% yield) | ≥ +8% | — | — | | |
| Cascade 3.000 pack (s) | < 60 | — | — | | |
| Test suite (phút) | < 10 | | | | |
| Mutation score domain (%) | ≥ 70 | — | — | — | |

`—` nghĩa là chỉ số đó chưa tồn tại ở milestone ấy, không phải "chưa đo".

---

## Chỉ số cố ý KHÔNG đo ở M0

Ghi lại để lần sau khỏi tưởng là quên:

| Chỉ số | Vì sao chưa |
|---|---|
| Thời gian `make ci` | C12 chỉ kiểm **đúng/sai** (exit 0 khi sạch, exit 2 khi phá format). Số giây chỉ có nghĩa khi đã có kha khá test — đo từ M2 |
| Throughput bất kỳ | Chưa có đường dữ liệu nào. Bắt đầu ở M2 |
| Độ trễ truy vấn | Chưa có read model. Bắt đầu ở M6 |
| Thời gian build | Solution mới có 3 project; số bây giờ không nói lên điều gì về sau |
