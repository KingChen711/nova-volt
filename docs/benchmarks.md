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

> [!warning] D1 chưa được đo đúng như đã phát biểu
> D1 nói *"máy sạch → `make up` < 5 phút"*. Ba lần đo trên đều chạy với **image đã nằm trong
> cache Docker**, tức là bỏ qua phần tải image — mà `mssql` một mình đã hơn 1 GB.
>
> Ba con số 48/36/42 s chứng minh phần **khởi động** rất thoải mái so với ngưỡng 300 s. Chúng
> **không** chứng minh vế "máy sạch". Muốn khép D1 cho chặt thì cần một lần đo sau
> `docker image prune -a`, và dòng đó chưa có ở đây.

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
