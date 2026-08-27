# NovaVolt Battery MES

Hệ thống MES / Traceability mô phỏng cho nhà máy sản xuất pin xe điện.

Đây là **learning project**. Mục tiêu là hiểu sâu ba thứ cùng lúc: nghiệp vụ sản xuất pin, kiến trúc backend .NET sát production, và mô hình phát triển của Siemens Opcenter Execution Foundation.

| | |
|---|---|
| **Backend** | .NET 10 LTS · SQL Server (event store, write model) · PostgreSQL + TimescaleDB (telemetry, read model) |
| **Messaging** | RabbitMQ (Manufacturing Service Bus) · EMQX (MQTT Sparkplug B) |
| **UI** | Mendix (toàn bộ) |
| **Trạng thái** | **M0 xong** 2026-08-26 · **M1 đang làm** — code xong, milestone chưa đóng vì D5 còn mở · [lộ trình 14 milestone](docs/scope.md#9-lộ-trình-milestone) |

---

## Quickstart

Mục tiêu: máy lạ chạy được trong **15 phút** chỉ bằng cách đọc mục này. Phải tra cứu ra ngoài
nghĩa là mục này còn thiếu — báo lại.

### Cần có trước

| | Bản | Kiểm bằng |
|---|---|---|
| **Docker Desktop** | quota RAM **≥ 8 GB** | Settings → Resources. Hạ tầng khai báo `mem_limit` tổng 6,00 GB |
| **.NET SDK** | **10.0.300** trở lên | `dotnet --version` |
| **GNU Make** | bất kỳ | `make --version` |
| **`sh`** | có sẵn cùng Git trên Windows | Makefile ép `SHELL := sh` để chạy giống nhau mọi nền tảng |
| Mendix Studio Pro | 11.12.3 | **Chỉ cần nếu đụng tới UI** — xem [`mendix/README.md`](mendix/README.md) |

Docker phải **đang chạy**. Thiếu bước này là lỗi hay gặp nhất, và thông báo của compose không
nói thẳng ra.

### Năm bước

```bash
cp .env.example .env          # 1. Không có .env thì make up dừng ngay, có hướng dẫn
make up                       # 2. Hạ tầng + khởi tạo DB/bucket. ~40–60 s khi image đã cache
make hooks                    # 3. Bật pre-commit hook — KHÔNG tự bật khi clone
make test                     # 4. 325 unit test, ~6 s
dotnet run --project src/Apps/Nvm.Host.All   # 5. Host .NET ở :5080
```

`make up` **là hai lệnh gộp** — `up --wait` rồi chạy profile `init`. Chạy tay `docker compose up`
sẽ có SQL Server nhưng **không** có database `NovaVolt`, và hỏng theo kiểu rất khó đoán.

**Bước 3 không bỏ được.** Git cố ý không kích hoạt hook từ repo khi clone. Không chạy `make hooks`
thì hook nằm trong `.githooks/` mà **không chạy**, và bạn sẽ tưởng format đang được kiểm.

Kiểm bước 5 ở cửa sổ khác:

```bash
curl http://localhost:5080/health/ready
```

Phải trả `"status":"Healthy"` kèm **6 check**: `sqlserver`, `postgres`, `rabbitmq`, `bus`,
`minio`, `keycloak`. EMQX **cố ý không** nằm trong readiness — host chưa dùng MQTT, xem
`docs/plans/M0-bootstrap.md` §C10.1.

`bus` và `rabbitmq` **không thay thế được cho nhau**, và cả hai đều phải có. `bus` là health check của
MassTransit: nó nói về bus **trong process này** — đã khởi động chưa. `rabbitmq` nói về **broker**. Đo
được: broker chết *trước* khi app khởi động thì cả hai bắt được; broker chết *sau* khi bus đã chạy thì
`bus` báo `Healthy` suốt **152 giây** trong khi `rabbitmq` bắt được ngay. Xoá `rabbitmq` vì *"MassTransit
đã có health check rồi"* là để đúng kịch bản nguy hiểm nhất — broker chết giữa ca — không ai canh
(`docs/benchmarks.md`).

### Bảng port

`.env` là nguồn sự thật; dưới đây là giá trị mặc định trong `.env.example`.

| Cổng | Service | Mở bằng trình duyệt | Tài khoản |
|---|---|---|---|
| 5080 | **Nvm.Host.All** (.NET, chạy ngoài Docker) | `/health/live` · `/health/ready` | — |
| 8080 | **Mendix** `NvmShopFloor` (Studio Pro, ngoài Docker) | `/oauth/v2/login` | `op.nv1` |
| 8081 | **Keycloak** | <http://localhost:8081> | `NVM_KEYCLOAK_ADMIN` |
| 15672 | **RabbitMQ** management | <http://localhost:15672> | `NVM_RABBITMQ_USER` |
| 18083 | **EMQX** dashboard | **không publish** — xem ghi chú dưới bảng | `NVM_EMQX_USER` |
| 9001 | **MinIO** console | <http://localhost:9001> | `NVM_MINIO_USER` |
| 9000 | MinIO API | — | — |
| 1433 | SQL Server | — | `sa` / `NVM_MSSQL_SA_PASSWORD` |
| 5432 | PostgreSQL + TimescaleDB | — | `NVM_POSTGRES_USER` |
| 5672 | RabbitMQ AMQP | — | — |
| 1883 | EMQX MQTT | **không publish** — xem ghi chú dưới bảng | — |
| 3000 | Grafana — profile `obs`, chưa bật tới M13 | — | — |

Mật khẩu nằm trong `.env` dưới đúng khoá ghi ở cột cuối. **Không chép chúng vào tài liệu** —
hai bản sẽ trôi khỏi nhau.

> [!important] EMQX không mở port nào ra host, và đó là điều bắt buộc
> EMQX là service duy nhất chạm `ot-net`. Publish port của nó ra host mở lại đúng đường mà
> K11 cấm: container trên `it-net` không tới được `nvm-emqx:1883`, nhưng tới được
> `host.docker.internal:1883`. Bind `127.0.0.1` **không** cứu — đo được, xem
> `docs/plans/M0-bootstrap.md` §C08.4.
>
> Cần MQTT hoặc dashboard API lúc dev thì bước vào vùng đệm thay vì kéo nó ra ngoài:
>
> ```
> make dmz-shell
> mosquitto_sub -h emqx -t '#' -v
> wget -qO- http://emqx:18083/status
> ```
>
> Kiểm ranh giới bất cứ lúc nào bằng `make net-check` (9 phép đo, exit ≠ 0 nếu thủng).

> **8080 là của Mendix, 8081 là của Keycloak.** Quyết định chốt ở C05 vì cả hai cùng mặc định
> 8080 và đổi Mendix phiền hơn. Đổi lại sau là phải sửa cả realm config.

### Lệnh hay dùng

`make` không tham số in ra toàn bộ danh sách. Bốn cái dùng nhiều nhất:

| Lệnh | Việc |
|---|---|
| `make ci` | Đúng chuỗi CI sẽ chạy: restore → format → build Release → test |
| `make down` | Dừng, **giữ** dữ liệu |
| `make down-v` | Dừng và **xoá volume** — mất sạch dữ liệu |
| `make backup` | `git bundle` toàn repo sang `NVM_BACKUP_DIR`, có `verify` |

### Khi hỏng

| Triệu chứng | Nhìn chỗ này trước |
|---|---|
| `make up` báo lỗi biến không resolve được | Chưa `cp .env.example .env` |
| `make up` treo ở `--wait` | Docker Desktop chưa chạy, hoặc quota RAM < 8 GB |
| `/health/ready` báo `Unhealthy[sqlserver]` | SQL Server khởi động lâu nhất — đợi thêm. App **không** crash, đó là chủ ý (N15) |
| Commit không bị chặn dù sai format | Chưa chạy `make hooks` |
| Mendix không đăng nhập được | [`mendix/README.md`](mendix/README.md) — cấu hình IdP là **dữ liệu runtime**, không đi theo Git |

---

## Cấu trúc thư mục

```
novavolt-mes/
├─ AGENTS.md              Nguyên tắc làm việc — ĐỌC TRƯỚC TIÊN
├─ docs/
│  ├─ scope.md            Scope & design đầy đủ (nghiệp vụ, kiến trúc, 14 milestone)
│  ├─ plans/              Plan chi tiết từng milestone, chia theo commit
│  ├─ adr/                Architecture Decision Records
│  ├─ oef-mapping.md      Khái niệm Opcenter → thành phần trong repo, kèm trạng thái
│  └─ benchmarks.md       Sổ ghi số đo — mọi con số trong repo phải có một dòng ở đây
│
├─ src/
│  ├─ Platform/           Horizontal layers: Contracts, Bus, EventStore, Kernel, Time, POM
│  ├─ FunctionalBlocks/   Vertical layers: mỗi FB là một bounded context
│  ├─ Apps/               Deployable: gom FB + expose Public Object Model
│  └─ Workers/            Ingestion, EdgeGateway, Projections, ErpGateway, Simulator
│
├─ tests/                 Unit · Architecture · Integration · Contract · Load · Chaos
├─ tools/                 Project template, Roslyn analyzer, solution CLI
├─ deploy/                Helm chart, k3d, script khởi tạo DB
└─ mendix/                Tài liệu về các app Mendix (app sống ở Team Server riêng)
```

---

## Bắt đầu từ đâu

| Bạn muốn | Đọc |
|---|---|
| Hiểu nguyên tắc làm việc trong repo | [`AGENTS.md`](AGENTS.md) |
| Hiểu nghiệp vụ và kiến trúc | [`docs/scope.md`](docs/scope.md) |
| Biết việc tiếp theo phải làm gì | [`docs/plans/M0-bootstrap.md`](docs/plans/M0-bootstrap.md) |
| Biết vì sao chọn công nghệ X | [`docs/adr/`](docs/adr/) |

---

## Quy ước quan trọng

Chi tiết ở [`AGENTS.md`](AGENTS.md). Ba điều dễ quên nhất:

1. **Không ai tự commit thay bạn.** Agent chuẩn bị thay đổi rồi dừng lại; bạn đọc `git diff` rồi tự commit.
2. **Docs là bản đồ, không phải đường ray.** Được phép làm trái nếu có lý do — nhưng phải nói ra và cập nhật docs.
3. **Cấm `DateTime.UtcNow`.** Dùng `TimeProvider`. Lý do: saga chạy nhiều ngày phải test được trong vài giây.

---

*Tài liệu nghiệp vụ nguồn và ghi chú học tập nằm trong Obsidian vault, không nằm trong repo này.*
