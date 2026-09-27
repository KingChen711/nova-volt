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
| projection | có, probe `--health-probe` | — | `--migrate` |
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

## Trạng thái

`helm lint` và `helm template` chạy trong CI (job `solution.yaml`). **Chưa cài lên cluster thật**: smoke trên k3d/kind cần
tải image node của cluster, chưa làm (xem `docs/plans/project-completion.md`).
