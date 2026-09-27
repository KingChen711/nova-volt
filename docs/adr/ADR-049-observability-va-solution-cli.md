# ADR-049 — OpenTelemetry chỉ ở host, telemetry best-effort; một solution.yaml cho hai mode

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-27 |
| **Liên quan** | `docs/scope.md` §5.4, §9/M13, §12.4 · ADR-019, ADR-023 |

---

## Context

M13 đòi: trace liền mạch MQTT → ingestion → domain → projection → OData → Mendix; business metric; tắt OTel Collector
mà app vẫn chạy; `solution-cli` đọc `solution.yaml` sinh cấu hình cho dev (một process) và prod (tách container);
version theo Functional Block với ma trận tương thích. Kernel và Functional Block không được phụ thuộc hạ tầng (K8/K9).

## Decision

- **Kernel dùng BCL**: `KernelTelemetry` là `ActivitySource`/`Meter` tên `NovaVolt.Kernel`. Mỗi command dispatch là một
  span `command <CommandType>` (tag site, command, outcome) và một điểm đếm `nvm.commands{command,outcome}`. Không có
  listener thì chi phí gần bằng không.
- **`Nvm.Observability`** (chỉ host reference): `AddNvmObservability(serviceName)` bật trace/metric/log OpenTelemetry
  1.17, nguồn `NovaVolt.*` và `MassTransit`, ASP.NET Core, HttpClient, runtime. Chỉ khi có `NVM_OTEL:Endpoint` mới xuất
  OTLP (gRPC), timeout mặc định 2 s. Bốn host đã bật: Execution, ProjectionWorker, Ingestion, EdgeGateway.
- **Telemetry là best-effort**: exporter chạy nền theo lô; collector tắt thì bỏ dữ liệu, không làm request lỗi.
- **Version theo FB**: `<Version>` trong csproj của từng Functional Block. `solution.yaml` khai báo App nào gồm FB version
  nào. Luật tương thích: cùng major, bản build không cũ hơn bản khai báo.
- **`tools/solution-cli`**: `validate`, `matrix`, `generate --mode monolith|distributed`. Cả hai mode sinh overlay
  `docker-compose.solution.yml` gắn profile `solution` cho đúng service cần bật; monolith thêm `run-monolith.sh` chạy
  `Nvm.Host.All` trên máy dev, distributed thêm Helm `values.yaml`. Manifest sai thì không sinh gì.

## Consequences

**Được**

- Trace context từ người gọi HTTP đi tiếp vào span domain (đã kiểm), và MassTransit mang trace qua bus sẵn có.
- Một manifest, hai mode, kiểm bằng `docker compose config` trên chính compose của repo.

**Mất / phải chịu**

- **Chưa có** `traceparent` qua MQTT: edge gateway ghi buffer append-only (ADR-028) và gửi lô protobuf sang ingestion;
  mang trace theo từng message cần đổi định dạng buffer. Trace hiện bắt đầu ở HTTP/bus, chưa từ thiết bị. DoD T9 chưa đạt.
- Chưa có OTel Collector/Tempo/Loki trong compose, chưa có dashboard SLO/error budget, business metric mới có
  `nvm.commands`. Chưa đo N1/N2 sau instrumentation (rig chưa qua preflight).
- `solution-cli` chưa có Helm chart để dùng `values.yaml`; chưa deploy k3d. Mode monolith vẫn bật `edge-gateway` vì
  compose gốc không gắn profile cho service đó.
- Version FB là khai báo tay trong csproj, chưa có package NuGet riêng từng FB.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Kernel reference OpenTelemetry API | Vi phạm K9 cho mọi FB; BCL `ActivitySource` đủ và OpenTelemetry lắng nghe được |
| Luôn xuất OTLP tới địa chỉ mặc định | Máy dev không có collector sẽ tốn timeout và log lỗi; bật khi có cấu hình |
| Hai file compose viết tay cho hai mode | Hai bản sẽ lệch nhau; scope đòi "cùng một code, chỉ khác solution.yaml" |

## Evidence

- `ObservabilityTests` (lab M13): endpoint OTLP không ai nghe; 200 request HTTP đều 200; span domain nằm trong trace của
  người gọi; `StopAsync` < 15 s.
- `SolutionCliTests` 8/8: manifest của repo hợp lệ với version build và phủ đúng mọi FB; luật major/minor; FB lạ, mode lạ,
  extension app trỏ App không tồn tại đều báo lỗi; cùng manifest sinh hai mode khác nhau.
- `dotnet run --project tools/solution-cli/Nvm.SolutionCli -- generate --mode …` rồi
  `docker compose -f docker-compose.yml -f artifacts/solution/<mode>/docker-compose.solution.yml --profile solution config --services`:
  monolith → hạ tầng (+ edge-gateway); distributed → hạ tầng + execution, projection, ingestion, edge-gateway.
