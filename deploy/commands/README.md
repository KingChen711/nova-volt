# Command store và data collection — M4/C05–C06

`Nvm.CommandStore` giữ claim, effect và outcome trong cùng transaction SQL Server.
Endpoint C06 ghi data collection, event và outbox trong cùng transaction; worker publish sau commit. Context SQL lấy từ cùng
`OperatorFixture.GenerateUnits()` với POM C03: 1.000 unit/site NV1 và DE1.

## Chạy local

SQL Server và database/user `NovaVolt`/`nvm_app` phải được khởi tạo bằng `make up` hiện có.
Credential nằm trong `.env` local, không thêm giá trị vào tài liệu hoặc git.

```sh
docker compose --profile execution build execution
docker compose --profile execution run --rm --no-deps execution-prepare-commands
docker compose --profile execution up -d --no-deps execution
```

Job thứ hai migrate và seed bằng principal migration; chạy lại không ghi đè context đã có.
Process phục vụ request không tự migrate. Readiness kiểm cả POM lẫn schema/quyền command store.
Job đòi database đã tồn tại, không tự tạo database hay xoay credential.

Ngoài Docker: `--migrate-commands` chỉ migrate, `--prepare-command-fixture` migrate và seed
(chỉ Development). Cả hai đọc `NVM_COMMANDS__MigrationConnectionString`.
Runtime đọc `NVM_COMMANDS__ConnectionString`; Development có thể dùng các khoá
`NVM_PORT_MSSQL`, `NVM_MSSQL_APP_PASSWORD` hiện có để dựng connection tới local `NovaVolt`.
Production bắt buộc connection riêng, không dùng fallback local.
`NVM_COMMANDS__CommandTimeoutSeconds` mặc định 30, cho phép 1–300 giây.

## Transaction và quyền SQL

- Đăng ký `AddNvmKernel` rồi `AddNvmCommandStore`. Store và `SqlCommandSession` là scoped.
  Handler nhận chính session đó; mọi lệnh SQL dùng `CreateCommand` và SQL parameters.
  Handler không mở connection khác hay tự commit transaction của session.
- Command implement `IDurableCommand`; `SiteId` và `ActorId` do server lấy từ principal đã xác thực.
  `CommandType="RecordDataCollection"` là token contract ổn định, không lấy từ tên class khi refactor.
  Backend kiểm key UUIDv5 bằng `(site, commandType, submissionId)` theo helper ADR-010.
- `CanonicalPayload` phải được mapper backend dựng tất định từ tất cả trường thuộc ý định gửi.
  Không truyền JSON thô có thứ tự property bất định; không đưa timestamp nhận request/correlation mới
  vào đó. C05 băm SHA-256 của biểu diễn này; C06 chịu trách nhiệm ánh xạ đầy đủ payload.
- Claim khóa `(SiteId, IdempotencyKey)` bằng unique PK và `UPDLOCK,HOLDLOCK`; transaction còn mở
  tới Complete/Abandon. Context reader ép site từ session, giữ read lock tới commit.
- Cùng key nhưng actor/type/hash/result type khác bị từ chối trước khi trả outcome.
  Hai command cùng scope bị chặn; request song song phải có scope riêng.
- Rejection nghiệp vụ vẫn lưu outcome, không ghi effect. Exception trước commit rollback cả claim
  và effect; không DELETE claim đã commit nếu mất phản hồi commit. Replay không gọi handler.
- Handler ghi event/outbox trước commit bằng cùng `SqlCommandSession`. Replay trả outcome đã lưu,
  không thêm event/intent. Worker publish sau commit và retry với ID cũ. Audit RAM chỉ ghi kết quả
  handler, không thay thế audit bền vững.

Schema ban đầu nằm trong `Nvm.CommandStore/Migrations/001-command-store.sql`, chạy idempotent trong
một transaction. Runtime `nvm_app` được SELECT/INSERT/UPDATE outcome, DENY DELETE;
context chỉ SELECT, DENY INSERT/UPDATE/DELETE. Các thay đổi schema về sau cần migration mới,
không sửa script 001 để kỳ vọng nó tự nâng một bảng đã tồn tại. Migration 002 thêm
`execution.DataCollection`: runtime SELECT/INSERT, DENY UPDATE/DELETE. Không có lệnh sửa/xoá kết quả
đã nhận; sửa sai nghiệp vụ thuộc bút toán bù trừ sau này.

Execution và Host.All đều từ chối cấu hình thiếu SQL ngoài Development; startup guard còn bắt
trường hợp đăng ký muộn kéo store về RAM. Command volatile cũ chỉ dùng RAM ở Development;
không đặt claim bền vững trước một effect FactoryModel còn nằm trong RAM.

## Command API C06

Hai host cùng mở `POST /api/v1/commands/production/record-data-collection`. Dùng Bearer token của
`Operator` hoặc `LineLeader`, đúng một `site_id` NV1/DE1 và một `sub` không rỗng. Actor luôn từ `sub`.
Body mẫu dưới đây có key UUIDv5 khớp submission; mỗi ý định mới cần submission mới, không dùng lại ví dụ
cho những lần nhập khác nhau.

```json
{
  "idempotencyKey": "e2a083f7-28f0-50c3-93f9-6977165c2339",
  "siteId": "NV1",
  "occurredAt": "2026-09-15T08:12:30.512+00:00",
  "payload": {
    "submissionId": "b7e2f0a1-3c4d-4e5f-a6b7-c8d9e0f1a2b3",
    "serial": "NV1PP16250A00001",
    "operationRunId": "OPRUN-NV1-EOL-0001",
    "stepCode": "EOL",
    "equipmentPath": "NOVAVOLT/NV1/PACK/P1/EOL-01",
    "signalCode": "PackVoltage",
    "value": 401.25,
    "unitOfMeasure": "V"
  }
}
```

| HTTP | Ý nghĩa / xử lý |
|---|---|
| 200, `accepted=true` | Một kết quả đo đã commit; không kết luận pack đạt chất lượng |
| 200, `accepted=false` | Rejection nghiệp vụ đã lưu: xem `reasonCode`, `reasonText`, `blockingRules`; replay giữ nguyên rejection |
| 400 | Input/key không hợp lệ, chưa claim; gồm thiếu `value`, `occurredAt` hoặc payload |
| 401 / 403 | Token/quyền/site không hợp lệ; không trả outcome cũ cho người đã mất quyền |
| 409 | Cùng key nhưng actor hoặc nội dung đã đóng băng khác; không ghi đè bản cũ |
| 503 / 500 / mất response | Chưa xác nhận kết quả; retry **cùng submission/key/payload**, không tự tạo ý định mới |

Fingerprint giữ toàn bộ payload và `occurredAt`, chuẩn hoá cùng instant/giá trị decimal.
Hai lần nhập khác submission vẫn là hai kết quả, dù giá trị giống nhau. Backend kiểm context SQL trong
transaction: đúng pack/operation run/EOL/trạm, `Running`, không `Held`/`Scrapped`. Kết quả được thêm mới;
context và POM không đổi state sau submit. `allowedNextActions` chỉ hướng tới danh sách/chọn unit khác;
lỗi hạ tầng dùng `RetrySameSubmission`, không đề nghị override/release.

Bind queue quan sát vào topic exchange `nvm.production-execution` **trước** submit, routing pattern
`nvm.*.production-execution.data-collection-recorded.v1`. Trong management UI, Get messages chọn
**requeue** khi xem bằng chứng. Đối chiếu `ce_id = message.eventId = idempotencyKey`, site, serial,
`value`, actor, `occurredAt` và `recordedAt` với row SQL. Golden dùng envelope CloudEvents; RabbitMQ
dùng body MassTransit và các header `ce_*` hiện có.

Execution đọc `NVM_RABBITMQ_HOST` (mặc định localhost), `NVM_PORT_RABBITMQ`, `NVM_RABBITMQ_USER` và
`NVM_RABBITMQ_PASSWORD`. Compose dùng host `rabbitmq`, port nội bộ 5672. Worker SQL outbox claim từng
row ngay trước publish; lease mặc định 2 phút, publish budget bằng nửa lease. Broker nhận rồi process
chết trước SQL ACK có thể giao lại cùng event ID: consumer phải chống trùng. CloudEvents headers lấy
từ envelope bất biến trong `es.Events`, kể cả khi host gửi lại đổi tên.

Trước khi chạy image mới, áp dụng schema (không reseed dữ liệu):

```sh
docker compose --profile execution build execution
docker compose --profile execution run --rm --no-deps execution-prepare-commands --migrate-commands
docker compose --profile execution up -d --wait --no-deps execution
```

Readiness kiểm quyền command, event store/outbox và Traceability bằng principal runtime. Thiếu quyền
trả 503, liveness vẫn sống. Migration dùng credential riêng, không trao quyền DDL cho `nvm_app`.

## Kiểm chứng

```sh
dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj -- --filter-class '*Command*'
dotnet test --project tests/Unit/Nvm.UnitTests/Nvm.UnitTests.csproj -- --filter-namespace 'Nvm.UnitTests.Commands'
dotnet test --project tests/Architecture/Nvm.ArchitectureTests/Nvm.ArchitectureTests.csproj
```

SQL tests dùng container riêng; không reset database đang chạy. `Nvm.CommandStoreProbe` tạo các
process độc lập để kiểm replay và kill trước commit. Credential chỉ đi qua environment của process,
không qua command line/output. Test effect C05 là bảng kiểm thử. `ExecutionCommandHttpTests` chạy API
C06 qua HTTP/JWT thật, hai process, mất ACK qua proxy, restart giữ DB và queue quan sát đã bind trước
publish. Broker/trigger lỗi chỉ áp dụng lên container riêng của test. Lab draft qua Mendix vẫn ở C09.

RED trên parent C04: export `43e4b05` vào thư mục tạm, chép riêng
`tests/TestAssets/Nvm.LegacyRestartProbe` vào cùng đường dẫn trong bản export rồi chạy project đó,
truyền một đường dẫn effect file chưa tồn tại. Probe chỉ gọi kernel API đã có trên parent:
hai process dùng store RAM ghi cùng ý định thành 2 effect; oracle đòi 1 và trả exit 1.
Không chép SQL store C05 vào parent. Chi tiết số đo ở `docs/benchmarks.md` mục M4/C05.

Package runtime: [Microsoft.Data.SqlClient 7.0.3](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.0.3),
MIT; local transaction theo [tài liệu Microsoft](https://learn.microsoft.com/en-us/sql/connect/ado-net/local-transactions).
