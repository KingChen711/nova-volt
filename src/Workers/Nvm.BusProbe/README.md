# Nvm.BusProbe — worker tạm để chứng minh bus chạy thật

> **Xoá ở M2.** Khi `Nvm.Ingestion` và `Nvm.EdgeGateway` ra đời, chúng là consumer thật và probe hết
> vai trò. Xoá cả thư mục này, cả `bus-lab.sh`, cả ba target `bus-*` trong `Makefile` —
> đừng để lại một project không ai chạy
> ([`M1-factory-model-bus.md`](../../../docs/plans/M1-factory-model-bus.md) §8).

Process **riêng** với `Nvm.Host.All`. Đây không phải chi tiết triển khai: D1 của M1 nói *"2 consumer
ở process khác nhận độc lập"*, và hai consumer nằm chung một container DI thì không phân biệt được
với fan-out thật.

## Ba consumer

| Role | Queue | Làm gì |
|---|---|---|
| `cache` | `nvm.factory-model.cache-updater` | Đóng vai service giữ cây nhà máy trong bộ nhớ |
| `audit` | `nvm.factory-model.audit-trail` | Đóng vai service ghi audit trail |
| `failing` | `nvm.factory-model.failing-probe` | **Luôn ném exception**, để `_error` queue có thật một message |

Chọn bằng `NVM_BUS_PROBE_CONSUMERS`, mặc định `cache,audit`:

```bash
NVM_BUS_PROBE_CONSUMERS=cache dotnet run          # chỉ cache-updater
NVM_BUS_PROBE_CONSUMERS=cache,audit,failing dotnet run
```

Một biến chứ không phải ba cờ, vì câu hỏi cần **tắt** consumer nhiều bằng bật: chỉ khi tắt
`audit-trail` rồi publish tiếp mới thấy queue của nó tăng `messages` trong khi `cache-updater` vẫn
nhận bình thường. Hai consumer cùng chạy thì hai dòng log giải thích được bằng *hai queue* mà cũng
giải thích được bằng *một queue bị đọc hai lần*.

## Chạy kịch bản

```bash
make up          # RabbitMQ phải sống trước
make bus-fanout  # D1
make bus-dlq     # D2
make bus-chaos   # D4 — lab phá hoại, ~1 phút
```

Ba target gọi [`bus-lab.sh`](bus-lab.sh): nó build, chạy host + probe, làm kịch bản, in số, rồi dọn.
Log để lại ở `artifacts/bus-logs/`.

## Vì sao không dùng Serilog

Cả repo dùng Serilog; worker này thì không. Đầu ra duy nhất của nó là mấy dòng log consumer làm bằng
chứng cho D1/D2, và console logger có sẵn của `Microsoft.Extensions.Hosting` in ra đúng như vậy.
Thêm một package vào một project sắp bị xoá là thêm một dòng phải gỡ sau.

## Bẫy đã gặp, ghi lại để khỏi mất buổi thứ hai

- **`kill` giết vỏ, không giết ruột.** `( ... ) &` trong shell làm `$!` trỏ vào subshell; giết
  subshell để lại `.exe` chạy tiếp. Lần đầu chạy `bus-fanout` có hai probe cũ vẫn đang đọc queue, và
  phép kiểm thứ ba của D1 cho kết quả **ngược** — queue của consumer "đã tắt" vẫn bằng 0. Cùng lý do
  không dùng `dotnet run` trong script.
- **`grep -c` in `0` và thoát `1`.** `$(grep -c ... || echo 0)` cho ra chuỗi hai dòng `"0\n0"`, và
  `[ ... -ge ... ]` báo `integer expected`.
- **`MT-Fault-RetryCount` đếm lần thử **lại**.** Giá trị `4` nghĩa là 5 lần chạy. Cùng cái bẫy
  off-by-one mà `NvmRetryPolicy.MaxAttempts` đã đặt tên để tránh.
