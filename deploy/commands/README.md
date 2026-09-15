# Command store SQL — M4/C05

`Nvm.CommandStore` giữ claim, effect và outcome trong cùng transaction SQL Server.
Chưa có endpoint ghi data collection; C06 bổ sung handler/HTTP/event. Context SQL lấy từ cùng
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

## Contract cho handler C06

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
- Publish nằm sau khi dispatcher trả về. C06 cần phân biệt xử lý mới/replay để không publish lại;
  C05 chưa cung cấp event delivery hay outbox. Audit RAM hiện ghi kết quả chạy handler, không chứng
  nhận SQL đã commit và không thay thế audit bền vững.

Schema ban đầu nằm trong `Nvm.CommandStore/Migrations/001-command-store.sql`, chạy idempotent trong
một transaction. Runtime `nvm_app` được SELECT/INSERT/UPDATE outcome, DENY DELETE;
context chỉ SELECT, DENY INSERT/UPDATE/DELETE. Các thay đổi schema về sau cần migration mới,
không sửa script 001 để kỳ vọng nó tự nâng một bảng đã tồn tại.

Execution và Host.All đều từ chối cấu hình thiếu SQL ngoài Development; startup guard còn bắt
trường hợp đăng ký muộn kéo store về RAM. Command volatile cũ chỉ dùng RAM ở Development;
không đặt claim bền vững trước một effect FactoryModel còn nằm trong RAM.

## Kiểm chứng

```sh
dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj -- --filter-class '*Command*'
dotnet test --project tests/Unit/Nvm.UnitTests/Nvm.UnitTests.csproj -- --filter-namespace 'Nvm.UnitTests.Commands'
dotnet test --project tests/Architecture/Nvm.ArchitectureTests/Nvm.ArchitectureTests.csproj
```

SQL tests dùng container riêng; không reset database đang chạy. `Nvm.CommandStoreProbe` tạo các
process độc lập để kiểm replay và kill trước commit. Credential chỉ đi qua environment của process,
không qua command line/output. Test effect là bảng kiểm thử; D4/D5 qua API nghiệp vụ còn ở C06/C09.

RED trên parent C04: export `43e4b05` vào thư mục tạm, chép riêng
`tests/TestAssets/Nvm.LegacyRestartProbe` vào cùng đường dẫn trong bản export rồi chạy project đó,
truyền một đường dẫn effect file chưa tồn tại. Probe chỉ gọi kernel API đã có trên parent:
hai process dùng store RAM ghi cùng ý định thành 2 effect; oracle đòi 1 và trả exit 1.
Không chép SQL store C05 vào parent. Chi tiết số đo ở `docs/benchmarks.md` mục M4/C05.

Package runtime: [Microsoft.Data.SqlClient 7.0.3](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.0.3),
MIT; local transaction theo [tài liệu Microsoft](https://learn.microsoft.com/en-us/sql/connect/ado-net/local-transactions).
