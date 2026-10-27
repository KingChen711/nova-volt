# Nvm.Analyzers — ép quy ước bằng compiler

Ràng buộc trong [`AGENTS.md`](../../../AGENTS.md) §4 chỉ có giá trị khi có thứ gì đó **chặn** người
vi phạm. Dự án này chạy 6 tháng, một người làm, không có reviewer — build phải là người gác cổng.

| ID | Cấm gì | Ràng buộc | Ra ở |
|---|---|---|---|
| `NVM001` | Đọc đồng hồ máy: `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow` | K1 | M1 · C15 |
| `NVM002` | `DateTime` trong event, entity, facet, **kể cả lồng trong generic / mảng / nullable** | K2 | M1 · C16; v2 R1 |
| `NVM003` | Event thiếu `[EventVersion(n)]`, hoặc `n < 1` | K6 | M1 · C16; v2 R1 |
| `NVM004` | `Query<T>` đọc entity của domain khác trong handler, pre-check, post-action | K8 | v2 R1 |

Các rule nhận ra type của Foundation **theo tên** (`ContractSymbols`), vì analyzer không reference được
assembly nó đang phân tích. Đổi tên một type ở Foundation thì rule im lặng; test analyzer dựng snippet
bằng đúng các tên đó.

### NVM002 soi những type nào

Hai điều kiện **HOẶC** — xem `ContractSymbols.IsPersistedContract`:

1. cài `Nvm.Foundation.IFoundationEvent` (lên bus, vào bảng event),
2. kế thừa `Nvm.Foundation.ModelObject`: entity và facet, tức cột của DB MOM.

### NVM004 lấy domain ở đâu

Domain của kiểu được đọc lấy từ `[Entity<TBlock>]`/`[Facet<TBlock>]`; domain của command lấy từ
`[Command<TBlock>]` của tham số đầu trong `ICommandHandler`/`IPreCheck`/`IPostAction`; cả hai rồi tra
`[FunctionalBlock(name, domain)]` của block. Thiếu một mắt xích thì rule im lặng; lúc chạy,
`ModelMisuseException` vẫn chặn.

`Mentions()` đệ quy qua `TypeArguments` và `ElementType`, nên `DateTime?`, `DateTime[]`,
`IReadOnlyList<DateTime>` và `Dictionary<string, DateTime[]>` đều bị bắt. Bản chỉ so kiểu ngoài cùng
là bản ai cũng viết đầu tiên, và nó qua được mọi test viết cho property thường.

Mức severity khai ở [`.editorconfig`](../../../.editorconfig), **không** chỉ trong
`DiagnosticDescriptor` — xem bẫy số 3 bên dưới.

## Vì sao ba rule này là lỗi build chứ không phải ghi chú review

Điểm chung: **cả ba đều hỏng ở nơi không ai đang nhìn.**

**NVM001.** Formation chạy hàng giờ, aging hàng tuần. Một saga chờ 12 ngày chỉ test được bằng cách
**tua đồng hồ**, mà đồng hồ đọc từ property tĩnh thì không tua được. Kết quả không phải "test khó
viết" — kết quả là test **không được viết**, và nhánh 12 ngày lên production mà chưa ai chạy qua nó
lần nào.

**NVM002.** `DateTime` mang cờ `Kind`, không mang offset, và cờ đó **không sống sót qua JSON**. Site
DE1 là Leipzig, có DST, nên mỗi mùa thu có một giờ xảy ra **hai lần**. Bản ghi truy vết viết trong giờ
đó mơ hồ vĩnh viễn, và event store là append-only — không sửa lại được. Lỗi theo mùa: chạy đúng suốt
mấy tháng, trên mọi máy, trong mọi test, rồi một sáng Chủ nhật tháng Mười thứ tự hai event đảo nhau.

**NVM003.** Cái nhãn version trông thừa đúng vào ngày viết nó, và không thêm được nữa đúng vào ngày
cần tới. Khi đã có hình dạng thứ hai thì payload v1 nằm sẵn trong store append-only và **không cái nào
nói nó là v1**. Chuỗi upcaster không có gì để rẽ nhánh, chỉ còn cách đoán theo field nào có mặt.

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
