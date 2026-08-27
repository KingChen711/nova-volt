# Nvm.Analyzers — ép quy ước bằng compiler

Ràng buộc trong [`AGENTS.md`](../../../AGENTS.md) §4 chỉ có giá trị khi có thứ gì đó **chặn** người
vi phạm. Dự án này chạy 6 tháng, một người làm, không có reviewer — build phải là người gác cổng.

| ID | Cấm gì | Ràng buộc | Ra ở |
|---|---|---|---|
| `NVM001` | Đọc đồng hồ máy: `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow` | K1 | M1 · C15 |

Mức severity khai ở [`.editorconfig`](../../../.editorconfig), **không** chỉ trong
`DiagnosticDescriptor` — xem bẫy số 3 bên dưới.

## Vì sao NVM001 là lỗi build chứ không phải ghi chú review

Formation chạy hàng giờ, aging hàng tuần. Một saga chờ 12 ngày chỉ test được bằng cách **tua đồng
hồ**, mà đồng hồ đọc từ property tĩnh thì không tua được. Kết quả không phải "test khó viết" — kết
quả là test **không được viết**, và nhánh 12 ngày lên production mà chưa ai chạy qua nó lần nào.

`TimeProvider` là đường nối: `TimeProvider.System` lúc chạy thật, `FakeTimeProvider` trong test nhảy
hai tuần trong một mili-giây.

## Khớp theo SYMBOL, không theo chữ

```csharp
using Clock = System.DateTime;
...
Clock.UtcNow          // vẫn bị bắt, và thông báo vẫn nói "DateTime.UtcNow"
```

Một rule grep chuỗi `"DateTime.UtcNow"` bị vô hiệu bởi đúng một dòng `using`. Rule dễ lách dạy người
ta cách lách, không dạy cách sửa.

## Ba cách analyzer chết âm thầm

Cả ba đều cho ra **cùng một triệu chứng: build xanh**. Đó là lý do bảng kiểm chứng của C15 bắt buộc
phải có bước "cố tình vi phạm rồi xem build đỏ".

| # | Nguyên nhân | Dấu hiệu |
|---|---|---|
| 1 | **Roslyn không khớp** — analyzer build trên `Microsoft.CodeAnalysis.CSharp` mới hơn compiler | `AD0001` / `CS8032`, mặc định là **warning** nên trôi trong log |
| 2 | **Thiếu `OutputItemType="Analyzer"`** trong `ProjectReference` | Không dấu hiệu nào. Nó thành reference thư viện bình thường |
| 3 | **Không khai severity trong `.editorconfig`** | Không dấu hiệu nào. Diagnostic mặc định `Info` không chặn build kể cả khi `TreatWarningsAsErrors` đang bật |

Bẫy 1 được chặn bằng cách pin version theo SDK — xem nhóm *"Roslyn analyzer"* trong
[`Directory.Packages.props`](../../../Directory.Packages.props).

## Nối vào project khác: `.targets`, không phải `.props`

`ItemGroup` nối analyzer nằm trong [`Directory.Build.targets`](../../../Directory.Build.targets).

`Directory.Build.props` nạp **trước** nội dung `.csproj`, nên `<IsAnalyzerProject>` khai trong
`Nvm.Analyzers.csproj` chưa tồn tại lúc điều kiện được đánh giá → điều kiện luôn đúng → analyzer
tham chiếu chính nó → MSBuild báo vòng phụ thuộc. `.targets` nạp **sau**, nên điều kiện đọc được.

Project không muốn bị nối (ví dụ `Nvm.AnalyzerTests`, cần tham chiếu thường để lấy `typeof`) khai
`<UseNvmAnalyzers>false</UseNvmAnalyzers>`.

## Thêm một diagnostic mới

1. Viết class analyzer, id `NVMnnn`.
2. Thêm dòng vào [`AnalyzerReleases.Unshipped.md`](AnalyzerReleases.Unshipped.md) — RS2008 sẽ báo
   lỗi nếu quên.
3. Khai severity trong `.editorconfig`.
4. Thêm test ở [`tests/Analyzers/Nvm.AnalyzerTests`](../../../tests/Analyzers/Nvm.AnalyzerTests),
   **kèm một trường hợp đối chứng** — code hợp lệ phải im lặng. Không có nó thì một analyzer báo lỗi
   mọi thứ vẫn qua được hết test.
5. Chạy lại bảng 5 bước của C15: cố tình vi phạm, xem build đỏ, hoàn nguyên, xem build xanh.
