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
- **Trace qua MQTT**: simulator mở span `mqtt publish` và gửi `traceparent` trong user property MQTT 5; edge gateway
  nhận (chỉ chuỗi đúng W3C), mở span `mqtt receive` dưới đó và ghi context vào trường `trace_parent` của
  `gateway_ingress.proto` (trường mới, bản ghi buffer cũ vẫn đọc được); ingestion mở span `ingest batch` link tới mọi trace
  trong lô và một span `ingest` con dưới từng trace thiết bị.
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

- Outbox của ingestion lưu `trace_parent` cạnh intent (migration 017) và dispatcher publish dưới trace đó. Nhưng hiện chỉ
  kết quả file drop được announce (reading Sparkplug chưa gắn unit id), nên trace MQTT thật chưa đi tới bus; đoạn domain → projection đi tiếp nhờ MassTransit, còn OData/Mendix là request riêng của người đọc (không
  cùng trace, chỉ nối được bằng link). DoD T9 "một trace liền mạch tới Mendix" chưa đạt.
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
- `MqttTraceContextTests`: publish của simulator qua mosquitto thật tới subscriber MQTT 5 mang `traceparent` đúng bằng id
  của span `mqtt publish`. `SparkplugIngressBatchCodecTests`: trường trace đi qua mã hoá lô và bản ghi không có trường
  vẫn đọc được; giá trị không đúng W3C bị bỏ.
- `SolutionCliTests` 8/8: manifest của repo hợp lệ với version build và phủ đúng mọi FB; luật major/minor; FB lạ, mode lạ,
  extension app trỏ App không tồn tại đều báo lỗi; cùng manifest sinh hai mode khác nhau.
- `dotnet run --project tools/solution-cli/Nvm.SolutionCli -- generate --mode …` rồi
  `docker compose -f docker-compose.yml -f artifacts/solution/<mode>/docker-compose.solution.yml --profile solution config --services`:
  monolith → hạ tầng (+ edge-gateway); distributed → hạ tầng + execution, projection, ingestion, edge-gateway.
- N14 (2026-09-27): integration suite 11m18s–13m02s khi mỗi lớp test có SQL Server riêng và mỗi test telemetry có
  TimescaleDB riêng; **7m53s** sau khi dùng chung một SQL Server và một TimescaleDB (database riêng cho mỗi fixture/test).
- Chaos SQL (`SqlOutageChaosLabTests`, NVM_RUN_LABS=1, 2026-09-27): SQL Server riêng bị `docker pause` 120 s; 24 submission
  mới trong lúc sập, mỗi cái tự gửi lại cùng submission. Hai lượt: readiness về sau 16,5 s và 17,6 s kể từ lúc SQL trở lại;
  mọi submission đúng 1 outcome, 1 event, 1 dòng outbox; trên bus 29/29 `ce_id` tới. Lượt 1 có một event tới hai lần (cùng
  `ce_id`, đúng at-least-once của ADR-040), lượt 2 không có bản trùng. `pause` giữ nguyên cổng; chưa thử `stop` hẳn process.
- Toàn solution trong container .NET SDK Linux như CI: 1.072/1.081 xanh, 8 skip, 7m56s (N14 < 10 phút đạt trên máy này);
  test đỏ còn lại là timeout mặc định của MassTransit harness khi chạy song song, đã nâng lên 30 s.
- `PostgresOutboxTests.IntentWithTraceContext_IsPublishedInsideThatTrace`: intent có `trace_parent` được publish với
  `Activity.Current` thuộc đúng trace đó.
- Chaos broker (`BrokerLatencyChaosLabTests`, NVM_RUN_LABS=1, Toxiproxy 2.12.0, 2026-09-27): +500 ms mỗi chiều trên
  AMQP. Nhận command p95 48 ms (không đổi so với 79 ms lúc thường), 20/20 event vẫn tới nhưng mất 20,7 s thay vì 2,2 s.
  Phần "alert bắn đúng" chưa làm: chưa có Prometheus/Alertmanager trong compose.
- Mutation (Stryker.NET 5.0, 2026-09-27): chạy được bằng `tests/Mutation/Nvm.DomainMutationTests` (xunit v2, VSTest);
  với xunit v3, test chạy trong process con nên Stryker không bật được mutant (0 bị giết). Điểm trên unit test domain:
  Equipment 28,4 %, Passport 29,7 %, Quality 12,9 %, Grading 11,7 %, Traceability 36,0 % — **trượt** ngưỡng 70 %. Phần lớn
  mutant "NoCoverage": handler chỉ được kiểm bằng integration test, mà Stryker không chạy được integration test. Trên code
  có unit test phủ: Equipment 77 %, Passport 80 %, Quality 71 %, Traceability 58 %, Grading 21 %.
