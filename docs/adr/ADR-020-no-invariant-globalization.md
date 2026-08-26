# ADR-020 — Không bật `InvariantGlobalization`

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/plans/M0-bootstrap.md` §C02.1 · `docs/scope.md` §2.3 · ADR-012 (chưa viết) |

---

## Context

Plan M0 bản đầu ghi bật `InvariantGlobalization: true` trong `Directory.Build.props`. Ý định
đúng: chặn mọi phép format và so sánh chuỗi phụ thuộc culture, thứ gây ra loại bug chỉ xuất hiện
trên máy có locale khác — `1,5` với `1.5`, `i` với `İ`.

Nhưng NovaVolt MES là hệ **multiplant**, và `scope.md` §2.3 yêu cầu đích danh hai site ở hai múi
giờ:

- **NV1** — `Asia/Ho_Chi_Minh` (+07:00, không có DST)
- **DE1** — `Europe/Berlin` (+01:00 / +02:00, **có DST**)

`IProductionCalendar` ở **M3** phải tính production day và shift boundary đúng cho cả hai, và
phải có test DST cho DE1. Toàn bộ chuyện đó đứng trên `TimeZoneInfo.FindSystemTimeZoneById` với
**ID kiểu IANA**.

Trên Windows, ánh xạ IANA → Windows time zone đi qua **ICU**. Chế độ globalization-invariant
không nạp ICU.

## Decision

**Không** bật `InvariantGlobalization`. Property này bị bỏ hẳn khỏi `Directory.Build.props`.

Mục tiêu ban đầu — chặn format phụ thuộc culture — được thay bằng hai biện pháp khác, cộng lại
mạnh hơn vì chúng báo lỗi **lúc build** thay vì lúc chạy:

1. **Analyzer trong `.editorconfig`:**
   ```ini
   dotnet_diagnostic.CA1305.severity = warning   # thiếu IFormatProvider
   dotnet_diagnostic.CA1310.severity = warning   # so sánh string không nêu StringComparison
   ```
2. **Set `CultureInfo` mặc định tường minh lúc khởi động** (C03), để hành vi không phụ thuộc
   locale của máy chạy.

## Consequences

**Được**

- `IProductionCalendar` ở M3 làm được đúng thứ `scope.md` §2.3 yêu cầu, kể cả test DST cho DE1.
- Vi phạm culture bị bắt **lúc build** ở đúng dòng code, thay vì thành `TimeZoneNotFoundException`
  lúc chạy ở site khác.
- Dùng được ID kiểu IANA — chuẩn duy nhất chạy giống nhau trên Windows và Linux container.

**Mất / phải chịu**

- Container phải nạp ICU → image lớn hơn và khởi động chậm hơn một chút so với chế độ invariant.
- Kỷ luật culture giờ nằm ở **analyzer**, mà `CA1305`/`CA1310` để mức `warning`, không phải
  `error`. Chúng **không** chặn build. Nếu về sau thấy có code lọt lưới thì nâng lên `error` —
  đó là thay đổi một dòng, không phải mở lại ADR này.
- Hành vi phụ thuộc dữ liệu ICU của hệ điều hành. Nâng cấp base image có thể đổi dữ liệu múi
  giờ; đó là chuyện tốt (bản vá DST) nhưng phải biết là nó tồn tại.

**Việc phát sinh**

- C03: set `CultureInfo` mặc định tường minh lúc khởi động.
- M3: test DST cho `Europe/Berlin` — ít nhất một ca sản xuất bắc qua thời điểm đổi giờ.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Bật invariant + tự map IANA → Windows ID** | Phải tự nuôi một bảng ánh xạ và tự cập nhật khi quy tắc DST đổi. Đúng việc mà ICU đã làm sẵn, làm đúng hơn. |
| **Bật invariant + dùng Windows time zone ID** (`SE Asia Standard Time`, `W. Europe Standard Time`) | Khoá dự án vào Windows. Deploy container Linux là hỏng ngay, mà `docker-compose` đã là một phần của M0. |
| **Giữ invariant, bỏ yêu cầu multiplant** | Đổi kiến trúc để chiều một cờ build. `SiteId` là first-class trong `scope.md` §5.6 và là nền của cross-site isolation ở M10. |
| **Nâng `CA1305`/`CA1310` lên `error` ngay** | Cân nhắc nghiêm túc, nhưng `TreatWarningsAsErrors: true` đã bật ở `Directory.Build.props` — nên `warning` ở đây thực tế đã chặn build trong project chính, và vẫn nới được ở `tests/` nơi đã tắt cờ đó. Để `warning` giữ được sự phân biệt ấy. |

## Evidence

Đo thực tế ngày 2026-08-25 trên Windows:

| Phép thử | `InvariantGlobalization=true` | `=false` |
|---|---|---|
| `FindSystemTimeZoneById("Asia/Ho_Chi_Minh")` | ❌ `TimeZoneNotFoundException` | ✅ +07:00 |
| `FindSystemTimeZoneById("Europe/Berlin")` | ❌ `TimeZoneNotFoundException` | ✅ hè +02:00 / đông +01:00 |
| `new CultureInfo("de-DE")` | ❌ crash | ✅ |

Đây là phép đo, không phải suy luận từ tài liệu — chạy lại được bằng một console app nhỏ với hai
giá trị property.

Hai dòng analyzer thay thế nằm ở `.editorconfig` dòng 116–117, kèm chú thích chỉ ngược về quyết
định này.
