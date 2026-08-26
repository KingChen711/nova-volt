# NovaVolt Battery MES

Hệ thống MES / Traceability mô phỏng cho nhà máy sản xuất pin xe điện.

Đây là **learning project**. Mục tiêu là hiểu sâu ba thứ cùng lúc: nghiệp vụ sản xuất pin, kiến trúc backend .NET sát production, và mô hình phát triển của Siemens Opcenter Execution Foundation.

| | |
|---|---|
| **Backend** | .NET 10 LTS · SQL Server (event store, write model) · PostgreSQL + TimescaleDB (telemetry, read model) |
| **Messaging** | RabbitMQ (Manufacturing Service Bus) · EMQX (MQTT Sparkplug B) |
| **UI** | Mendix (toàn bộ) |
| **Trạng thái** | M0 — Bootstrap · [lộ trình 14 milestone](docs/scope.md#9-lộ-trình-milestone) |

---

## Quickstart

> Phần này được điền ở **C16**, sau khi toàn bộ hạ tầng chạy được.
> Tiêu chí nghiệm thu: một dev chưa biết gì về repo này chạy được trong **15 phút** chỉ bằng cách đọc mục này.

<!-- TODO(C16): prerequisites, make up, make test, bảng port, URL các UI -->

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
