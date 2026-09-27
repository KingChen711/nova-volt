# Helm chart `novavolt`

Chart triển khai App Execution và ba worker (projection, ingestion, edge-gateway) lên Kubernetes. Danh sách workload,
image, tag và số replica **không** viết tay: `tools/solution-cli` sinh chúng từ `solution.yaml`.

```sh
dotnet run --project tools/solution-cli/Nvm.SolutionCli -- generate --mode distributed --out out/distributed
helm lint deploy/helm/novavolt -f out/distributed/values.yaml
helm template novavolt deploy/helm/novavolt -f out/distributed/values.yaml
```

## Chart tạo gì

| Workload | Deployment | Service | Migration (hook pre-install/pre-upgrade) |
|---|---|---|---|
| execution | có, probe HTTP `/health/ready` + `/health/live` | 8080 | `--migrate-commands` |
| projection | có, probe `--health-probe` | — | `--migrate` (sau execution: cấp quyền trên `es.Events`) |
| ingestion | có, probe `--health-probe` | 8080 | `--migrate` |
| edge-gateway | có | — | — |

Annotation `novavolt.io/fb-<tên>` trên Deployment của App ghi version từng Functional Block, lấy từ `solution.yaml`.

Hạ tầng (SQL Server, TimescaleDB, RabbitMQ, Keycloak, EMQX, MinIO) nằm ngoài chart. Tên host mặc định trong
`values.yaml` (`rabbitmq`, `emqx`, `ingestion`) giả định chúng có Service cùng tên trong namespace.

## Credential

Chart không tạo Secret và không nhận mật khẩu qua values. Trước khi cài, tạo cho mỗi workload:

- `<release>-<workload>`: biến môi trường runtime có mật khẩu, ví dụ `NVM_COMMANDS__ConnectionString`,
  `NVM_POM__ConnectionString`, `NVM_RABBITMQ_USER`, `NVM_RABBITMQ_PASSWORD` cho execution.
- `<release>-<workload>-migrate`: chuỗi kết nối của principal migration (`NVM_COMMANDS__MigrationConnectionString`,
  `NVM_PROJECTIONS__MigrationConnectionString`, ...), chỉ Job migration đọc.

Tên biến giống hệt khối `environment` của service tương ứng trong `docker-compose.yml`.

## Edge gateway: một replica cho mỗi client id

Edge gateway nối EMQX bằng client id cố định (`NVM_EDGE__ClientId`, mặc định `nvm-edge-gateway`). Hai process cùng client
id thì EMQX cho hai phiên giành nhau mỗi vài giây và cả hai liên tục đăng ký lại. Chart từ chối render khi edge-gateway có
hơn 1 replica. Cài song song với một gateway khác (ví dụ compose) thì đặt client id riêng trong Secret.

## Trạng thái

`helm lint` và `helm template` chạy trong CI (job `solution.yaml`). Smoke trên kind: `bash deploy/kind/smoke.sh` (mượn hạ
tầng của compose, xem đầu script). Lần chạy 2026-09-27 (kind v0.32.0, Kubernetes v1.35.0): 3 job migration xong, 5 pod
Ready, `/health/ready` của execution qua cluster trả 200, edge-gateway đăng ký được EMQX. Smoke chưa chạy trong CI.
