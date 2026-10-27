# NovaVolt MOM v2

Bản mô phỏng một hệ MOM (Manufacturing Operations Management) cho nhà máy module pin, dựng lại cơ chế
của một nền kiểu Opcenter EX bằng .NET để luyện kiểm thử quy trình, xử lý sự cố và viết tính năng theo
cách một team dự án mở rộng sản phẩm của nhà cung cấp. Mọi tên công ty, sản phẩm, hệ thống là hư cấu.

| | |
|---|---|
| **Backend** | .NET 10 · SQL Server (DB MOM của plant) · PostgreSQL (LineDB phía xưởng) |
| **Messaging** | RabbitMQ (bus event) · Redpanda/Kafka (gateway tích hợp) |
| **UI** | Next.js (từ R2) |
| **Trạng thái** | **R0 xong** · R1–R13 còn mở. Xem [tracker](docs/plans/tracker.md) và [scope](docs/scope.md) |

v1 (MES pin "trung tính", M0–M13) nằm ở tag [`v1.0`](https://github.com/KingChen711/nova-volt/tree/v1.0); vì sao dựng lại: [ADR-051](docs/adr/ADR-051-rebuild-theo-mo-hinh-mom-that.md).

---

## Quickstart

### Cần có trước

| | Bản | Kiểm bằng |
|---|---|---|
| **Docker Desktop** | quota RAM ≥ 8 GB | Settings → Resources |
| **.NET SDK** | 10.0.300 trở lên | `dotnet --version` |
| **GNU Make**, **`sh`** | bất kỳ (`sh` đi cùng Git trên Windows) | `make --version` |
| **Python 3** | bất kỳ | dùng cho kiểm clean-room |

### Các bước

```bash
cp .env.example .env          # 1. rồi điền mọi CHANGE_ME_*
make up                       # 2. hạ tầng + tạo database, chờ tới khi healthy
make hooks                    # 3. bật pre-commit hook (format + clean-room); Git không tự bật khi clone
make ci                       # 4. clean-room -> restore -> format -> build -> test
```

Máy từng chạy v1: dừng stack cũ trước (`docker compose down --remove-orphans`). Volume dữ liệu v1 không bị xóa.

### Bảng port

`.env` là nguồn sự thật; dưới đây là mặc định trong `.env.example`. Mọi port bind `127.0.0.1`.

| Cổng | Service | Tài khoản |
|---|---|---|
| 1433 | SQL Server | `sa` / `NVM_MSSQL_SA_PASSWORD` |
| 5672 / 15672 | RabbitMQ / giao diện quản trị | `NVM_RABBITMQ_USER` |
| 8081 | Keycloak | `NVM_KEYCLOAK_ADMIN` |
| 5432 | LineDB (Postgres phía xưởng) | `NVM_LINEDB_USER` |
| 19092 | Redpanda (Kafka API) | — |
| 1025 / 8025 | Mailpit SMTP / giao diện xem email | — |

Mật khẩu nằm trong `.env`; không chép vào tài liệu.

### Lệnh hay dùng

`make` không tham số in danh sách đầy đủ.

| Lệnh | Việc |
|---|---|
| `make ci` | Đúng chuỗi CI |
| `make cleanroom` | Kiểm repo không chứa tên, mã của dự án thật (K14) |
| `make down` / `make down-v` | Dừng, giữ / xóa volume |
| `make backup` | `git bundle` toàn repo sang `NVM_BACKUP_DIR`, có `verify` |

---

## Cấu trúc thư mục

```
├─ AGENTS.md            Nguyên tắc làm việc — ĐỌC TRƯỚC TIÊN
├─ docs/
│  ├─ scope.md          Scope v2: bốn lớp, cơ chế nền, lộ trình R0–R13, lab sự cố
│  ├─ plans/tracker.md  Trạng thái từng mốc và nợ mở
│  ├─ adr/              Architecture Decision Records
│  ├─ oef-mapping.md    Bảng cơ chế nền và test chứng minh
│  └─ glossary.md       Thuật ngữ
├─ src/
│  ├─ Foundation/       Cơ chế nền (vai nền tảng của nhà cung cấp)
│  ├─ Product/          Vai sản phẩm của nhà cung cấp
│  ├─ Project/          Vai team dự án
│  ├─ Shopfloor/        LineDB, line simulator, relay
│  └─ Integration/      Gateway, hệ ngoài giả lập
├─ tests/               Unit · Architecture · Analyzers
├─ tools/               Analyzer, kiểm clean-room
└─ deploy/              Script khởi tạo SQL Server, realm Keycloak
```

---

## Quy ước quan trọng

Chi tiết ở [`AGENTS.md`](AGENTS.md). Ba điều dễ quên nhất:

1. **Clean-room (K14).** Không đưa tên, mã, code của dự án thật vào repo; hook và CI chặn.
2. **Docs là bản đồ, không phải đường ray.** Được làm trái nếu có lý do, nhưng phải nói ra và sửa docs.
3. **Cấm `DateTime.UtcNow`.** Dùng `TimeProvider` để test được lịch ca và timer bằng đồng hồ giả.
