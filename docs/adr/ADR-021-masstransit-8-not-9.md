# ADR-021 — Pin MassTransit 8, không nâng lên 9

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/plans/M1-factory-model-bus.md` §2.1, §C09.1 · ADR-004 · `docs/scope.md` §10.1 (ghi chú FluentAssertions) |

---

## Context

M1 dựng Manufacturing Service Bus trên RabbitMQ (ADR-004). Thư viện client mặc định của hệ sinh
thái .NET cho việc này là **MassTransit**, và `scope.md` §9/M1 nêu đích danh nó.

Bản stable mới nhất trên NuGet lúc quyết định là **9.2.0**. Phản xạ thông thường là pin bản mới
nhất.

Kiểm nuspec ngày **2026-08-26**:

```
masstransit 9.2.0
    <licenseUrl>https://massient.com/license</licenseUrl>
    <projectUrl>https://massient.com/</projectUrl>

masstransit 8.5.10
    <license type="expression">Apache-2.0</license>
    <licenseUrl>https://licenses.nuget.org/Apache-2.0</licenseUrl>
    <projectUrl>https://masstransit.io/</projectUrl>
```

Đọc chính văn bản license tại `massient.com/license` (cập nhật 2026-05-04), **§3 Legacy Versions**:

> *MassTransit v8 và các bản trước vẫn free và open source theo license gốc của chúng. Tuy nhiên
> chúng **không được hỗ trợ** theo Thoả thuận này. Thoả thuận này chỉ áp dụng cho MassTransit v9
> trở lên.*

License v9 tính phí theo **product line** hoặc **toàn tổ chức**, không theo message volume.

Đây là learning project nên dùng v9 không vi phạm gì. Nhưng mục tiêu của dự án là **tập thói quen
đúng cho dự án thật** (`AGENTS.md` §0), và một thư viện thương mại lẻn vào qua một lần
"update all packages" là đúng loại rủi ro mà một tech lead sẽ phải trả lời trước audit.

## Decision

Pin **`MassTransit 8.5.10`** và **`MassTransit.RabbitMQ 8.5.10`** trong `Directory.Packages.props`.
Không nâng lên 9.x.

Quyết định được **ép bằng test**, không chỉ bằng comment: `MassTransitPinTests` đọc
`typeof(IBus).Assembly.GetName().Version.Major` và đỏ nếu nó khác 8.

Không thuộc quyết định này: chọn RabbitMQ (ADR-004), và topology cụ thể (C10).

## Consequences

**Được**

- License **Apache-2.0**, không có nghĩa vụ tài chính, không có điều khoản audit.
- `8.5.10` có nhóm `targetFramework="net10.0"` và kéo `RabbitMQ.Client 7.2.1` — tương thích
  RabbitMQ 4.3.5 trong compose. Kiểm bằng restore sạch, không `NU1605`/`NU1608`.
- Toàn bộ tính năng M1 cần (topology, retry, `_error`/`_skipped` queue) đều có ở v8.

**Mất / phải chịu**

- **Không có bản vá mới cho v8.** Đây là cái mất thật và không có cách nào bù. Nếu v8 lộ lỗ hổng
  bảo mật, lựa chọn còn lại là trả tiền hoặc đổi thư viện.
- **Tài liệu trên masstransit.io sẽ dần nói về v9.** Đọc docs phải để ý version, và một số hướng
  dẫn sẽ không áp dụng được.
- **Không có tuỳ chọn jitter cho retry.** v8 chỉ có `Immediate` / `Interval` / `Intervals` /
  `Exponential` / `Incremental`. `scope.md` §9/M1 ghi "exponential + jitter" nên phải tự cộng nhiễu
  bằng `Intervals` — xem plan M1 §C11.1. *(Chưa kiểm v9 có thêm jitter hay không; không ảnh hưởng
  quyết định.)*
- **Một ngày nào đó v8 sẽ không còn build cho TFM mới.** Lúc đó quyết định này phải mở lại chứ
  không được lách. Test thứ hai trong `MassTransitPinTests` canh đúng điều kiện đó.

**Việc phát sinh**

- `MassTransitPinTests` phải tồn tại và không được sửa số cho xanh. Test đỏ = đọc ADR này trước.
- Khi Dependabot/Renovate được bật (M13), phải cấu hình **chặn** major update cho hai package này.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **MassTransit 9.x** | License thương mại. Không sai cho learning project, nhưng tạo thói quen sai và làm repo không copy được sang dự án thật mà không kiểm lại |
| **Trả tiền license v9** | Không có ngân sách, và không học thêm được gì so với v8 ở phạm vi M1–M13 |
| **`RabbitMQ.Client` trần, không framework** | Phải tự viết retry, DLQ, serializer, topology, consumer lifecycle. Đó là 2–3 milestone công việc để học lại thứ đã có sẵn — và `scope.md` §5.2 muốn ánh xạ khái niệm OEF, không muốn viết lại một message framework |
| **NServiceBus** | Cũng thương mại, đắt hơn |
| **Rebus** | MIT, nhẹ, dùng được. Loại vì hệ sinh thái và tài liệu mỏng hơn nhiều, và vì `scope.md` nêu đích danh MassTransit — lệch khỏi đó cần lý do mạnh hơn "cũng được" |
| **Wolverine** | MIT, hiện đại. Loại cùng lý do Rebus, cộng thêm: nó gắn chặt với Marten, mà ADR-003 đã định hướng **không** dùng Marten |

## Evidence

**1. Hai dòng nuspec** — lệnh tái lập được, chạy 2026-08-26:

```bash
curl -s "https://api.nuget.org/v3-flatcontainer/masstransit/9.2.0/masstransit.nuspec"  | grep -i "<license\|projectUrl"
curl -s "https://api.nuget.org/v3-flatcontainer/masstransit/8.5.10/masstransit.nuspec" | grep -i "<license\|projectUrl"
```

Output đã trích ở phần Context.

**2. v8 hỗ trợ net10.0** — nhóm dependency trong nuspec `8.5.10`:

```
<group targetFramework="net10.0">
  <dependency id="MassTransit" version="8.5.10" />
  <dependency id="RabbitMQ.Client" version="7.2.1" />
</group>
```

**3. Restore sạch với central transitive pinning** — `dotnet restore NovaVolt.Mes.slnx` exit 0,
không có `NU1605` (hạ cấp version) hay `NU1608` (version ngoài khoảng cho phép). Quan trọng vì
`CentralPackageTransitivePinningEnabled` đang bật từ M0/C02, và MassTransit kéo theo
`Microsoft.Extensions.* 10.0.0` — đúng version host đang dùng, nên không có xung đột.

**4. Quyết định được ép bằng máy** — `MassTransitPinTests` trong `tests/Unit/Nvm.UnitTests/Bus/`.
Đây là lý do ADR này không chỉ là một tài liệu: nâng major sẽ làm `make ci` đỏ với thông báo trỏ
thẳng về đây.

> Đây là lần **thứ hai** trong repo một thư viện đổi license làm lệch quyết định. Lần đầu là
> FluentAssertions v8 (`scope.md` §10.1), giải bằng Shouldly. Quy tắc rút ra và áp từ nay:
> **kiểm license trước khi pin version, không chỉ kiểm nó có chạy không.**
