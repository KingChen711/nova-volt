# ADR-019 — Dùng .NET 10 LTS thay vì .NET 9

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-26 |
| **Liên quan** | `docs/plans/M0-bootstrap.md` §2.1, §C02 · `docs/scope.md` frontmatter `stack` (đã cập nhật) |

---

## Context

`scope.md` bản đầu ghi **.NET 9**. Lúc dựng máy thật ngày 2026-08-25 phát hiện hai điều:

1. SDK cài trên máy là **10.0.300**. Không có SDK 9 nào cả.
2. .NET 9 là bản **STS** — vòng đời 18 tháng, hết hỗ trợ khoảng **5/2026**. Tức là tại thời điểm
   bắt đầu dự án, nó **đã** hết nhận bản vá bảo mật.

Điểm hai mới là điểm quyết định. Điểm một chỉ là tiện.

Đây là dự án học kéo dài 14 milestone; chọn runtime ở M0 là chọn cho cả năm sau. Bắt đầu trên một
runtime đã hết hỗ trợ nghĩa là biết trước sẽ phải nâng cấp giữa chừng, ở thời điểm không do mình
chọn.

## Decision

Dùng **.NET 10 LTS**. Pin SDK trong `global.json`:

```json
{ "sdk": { "version": "10.0.300", "rollForward": "latestFeature", "allowPrerelease": false } }
```

`TargetFramework: net10.0` đặt một lần trong `Directory.Build.props` ở root, mọi project thừa
hưởng.

Đã cập nhật `docs/scope.md` cho khớp — plan và scope không được nói hai con số khác nhau.

## Consequences

**Được**

- Runtime còn trong vòng hỗ trợ suốt thời gian dự án; không có lần nâng cấp bắt buộc nào chen
  ngang.
- `TimeProvider` và `FakeTimeProvider` có sẵn — nền cho ràng buộc **K1** (không `DateTime.Now`
  trong domain), dùng từ M7.
- `Guid.CreateVersion7()` có sẵn — dùng cho khoá tăng dần theo thời gian, tránh phân mảnh index.
- `rollForward: latestFeature` cho phép cài bản 10.0.4xx sau này mà không phải sửa `global.json`,
  nhưng vẫn chặn nhảy sang major khác.

**Mất / phải chịu**

- Không có gì đáng kể. Mọi package trong scope đều có bản `net10.0` (kiểm ngày 2026-08-25:
  `Microsoft.Extensions.Hosting` 10.0.11, `Serilog.AspNetCore` 10.0.0, `xunit.v3` 4.0.0,
  `Microsoft.NET.Test.Sdk` 18.9.0, `Shouldly` 4.3.0).
- Tài liệu và câu trả lời trên mạng phần lớn viết cho .NET 8/9 — thỉnh thoảng phải đối chiếu lại
  API.

**Việc phát sinh**

- Máy nào clone repo cũng phải có SDK 10.0.300 trở lên. `global.json` sẽ báo lỗi rõ ràng nếu
  thiếu, nên không cần kiểm tra thêm.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Cài thêm SDK 9 để bám đúng scope.md** | Là cách chọn một runtime đã hết hỗ trợ bảo mật, chỉ để khỏi phải sửa một dòng tài liệu. Sai thứ tự ưu tiên — `AGENTS.md` §2 nói rõ docs là bản đồ, không phải đường ray. |
| **Không pin SDK**, để `dotnet` tự chọn | Máy khác bản SDK khác → build khác nhau, và lỗi chỉ lộ ra ở CI. `global.json` là bốn dòng. |
| **Pin cứng `rollForward: disable`** | Chặt quá: mọi bản vá SDK đều thành thay đổi phải commit. `latestFeature` cân bằng đúng chỗ. |

## Evidence

```
$ dotnet --version
10.0.300
```

- `global.json` trong repo phản ánh đúng quyết định này.
- Phân tích đầy đủ: `docs/plans/M0-bootstrap.md` §2.1.

Ngày hết hỗ trợ của .NET 9 lấy từ chính sách vòng đời STS (18 tháng kể từ phát hành 11/2024),
không đo được trong repo.
