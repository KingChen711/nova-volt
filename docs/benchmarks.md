# Sổ ghi số đo

Mọi con số trong repo này phải có một dòng ở đây. Không có dòng thì không được nhắc tới con số
đó ở chỗ khác. Số của v1 nằm ở tag `v1.0`.

## Luật ghi

1. **Chỉ số thật.** Không ước lượng, không "khoảng", không "tầm". Chưa đo thì để trống, đừng đoán.
2. **Điều kiện đo là bắt buộc.** Một con số không kèm điều kiện là một con số vô nghĩa.
3. **Một phép đo = một dòng.** Đo ba lần thì ba dòng, đừng lấy trung bình rồi ghi một dòng. Độ
   phân tán là thông tin.
4. **Đo lại thì thêm dòng mới**, không sửa dòng cũ. Số cũ cho biết thay đổi nào làm chậm đi.
5. Cột `Commit` là commit **tại thời điểm đo**. Đo trên thay đổi chưa commit thì ghi tag của mốc sẽ
   chứa thay đổi đó (`r1`, `r2`…): mỗi mốc là một commit có tag (AGENTS.md §1.1).

Máy đo: Windows 11, 16 luồng CPU, Docker Desktop (WSL2), quota RAM 8 GB.

---

## Stack v2 (`make up`)

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-10-27 | tag `r1` | `make up` tới khi mọi service healthy | **12 s** | Image đã có trong cache, container đã tạo từ lần chạy trước; gồm `mssql-init` |
| 2026-10-27 | tag `r1` | Tổng `mem_limit` khai báo | **5,125 GiB** | 6 service: mssql 2 GiB, keycloak 1 GiB, redpanda 1 GiB, rabbitmq 512 MiB, linedb 512 MiB, mailpit 128 MiB |
| 2026-10-27 | tag `r1` | RAM thực dùng lúc rảnh | **1.676 MiB** | `docker stats` ngay sau `make up`; không tải; mssql 756, keycloak 640, rabbitmq 139, redpanda 103, linedb 32, mailpit 6 |

## R1 — Foundation: command pipeline

Phép đo: `CommandLatencyBenchmark.One_thousand_sequential_commands` (build Release). Mỗi command
`CreateLabOrder` đi đủ pipeline: kiểm model, pre-check của app, handler (một `ProjectionQuery` sang
master data, một `Submit`, một event), hai post-action (một ghi facet), commit, phát event. 50 command
khởi động không tính. SQL Server 2022 CU26 trong Testcontainers, cùng máy; DB mới cho mỗi lần chạy.

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-10-27 | tag `r1` | p95 / p50 / p99, 1.000 command tuần tự | **14,7 / 10,6 / 17,6 ms** | Lần 1; tổng 11,3 s, max 25,4 ms |
| 2026-10-27 | tag `r1` | p95 / p50 / p99, 1.000 command tuần tự | **14,7 / 10,9 / 17,4 ms** | Lần 2; tổng 11,4 s, max 25,1 ms |
| 2026-10-27 | tag `r1` | p95 / p50 / p99, 1.000 command tuần tự | **14,7 / 10,9 / 17,6 ms** | Lần 3; tổng 11,4 s, max 23,0 ms |

Ngưỡng của test chốt sau ba lần đo trên: **p95 < 50 ms**, khoảng ba lần số đo, để chừa runner CI
chậm hơn máy cá nhân. Chưa đo trên runner GitHub.
