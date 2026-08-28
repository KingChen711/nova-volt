# Nvm.ArchitectureTests — ranh giới phải đỏ được

Sơ đồ ở [`scope.md`](../../../docs/scope.md) §5.4 chỉ có giá trị khi có thứ gì đó **đỏ** lúc ai đó vẽ
một mũi tên mới. Bảy rule dưới đây là thứ đó.

| # | Rule | Ràng buộc |
|---|---|---|
| A1 | `Nvm.Contracts` chỉ reference BCL | nền của K8 |
| A2 | `Nvm.Kernel` không biết MassTransit / EF Core / Npgsql / SqlClient | K9 |
| A3 | Functional Block chỉ được reference `Nvm.Contracts` và `Nvm.Kernel` | K8 |
| A4 | Không event nào chứa `DateTime`, kể cả lồng trong generic | K2 |
| A5 | Mọi event có `SiteId` **không nullable**, có `[EventContract]` và `[EventVersion(n≥1)]` | K3, K6 |
| A6 | `Nvm.FactoryModel` không reference `Nvm.Bus` | FB không tự chọn transport |
| A7 | Type sinh từ `sparkplug_b.proto` không ra khỏi `Nvm.Sparkplug`, và không assembly nào khác reference `Google.Protobuf` | `ADR-026` — hình dạng của một file mình không được sửa |

## Vì sao A3 là allowlist chứ không phải denylist

Denylist (*"FB không được reference FB khác"*) phải được sửa mỗi lần thêm một block, bởi chính người
thêm block đó, trong một file họ không có lý do gì để mở. Block đầu tiên bị quên chính là block mà
rule sinh ra để canh.

Allowlist (*"FB chỉ được reference Contracts và Kernel"*) từ chối mọi thứ mới **theo mặc định**. M2
thêm `Nvm.Traceability` thì A3 đỏ ngay mà không ai phải sửa gì.

## Vì sao A4 lặp lại NVM002 — và đã chạy thử

Analyzer chạy **bên trong compiler** và tắt được bằng một dòng, nằm ngay trong file đang vi phạm:

```
#pragma warning disable NVM002
```

Đã chạy thật (M1/C17.2): build `Nvm.Contracts` cho **0 lỗi NVM002**, và A4 vẫn **đỏ**. Architecture
test đọc metadata của assembly **đã sinh ra**, nơi `#pragma` không để lại dấu vết — property hoặc
mang kiểu `DateTime`, hoặc không.

Hai lớp bắt hai loại lách khác nhau. Không lớp nào thừa.

## Mỗi rule có một đối chứng dương

Các test `*_Control_*` không kiểm code production. Chúng kiểm **chính phép kiểm của rule**, bằng cách
áp nó lên một thứ *phải* bị bắt.

Lý do: một rule nhìn nhầm chỗ — danh sách type rỗng, tiền tố namespace gõ sai — báo *"không có vi
phạm"* và xanh vĩnh viễn. `ThereAreEventsToCheckAtAll` là ví dụ rõ nhất: bộ lọc event hỏng thì **mọi**
assertion của A4 và A5 đều đúng một cách vô nghĩa.

Cùng bài học với [C15.1](../../../docs/plans/M1-factory-model-bus.md) (analyzer im lặng là chế độ
hỏng mặc định), chuyển sang tầng test.

## Hai cái bẫy đã gặp

**`GetReferencedAssemblies()` không phải danh sách `ProjectReference`.** Reference mà thứ duy nhất
được dùng là một `const` sẽ bị compiler xoá sạch — giá trị nội tuyến vào IL, assembly biến mất khỏi
metadata. `Nvm.Bus` reference `Nvm.Hosting` trong csproj chỉ để lấy `HealthTags.Ready`, và
`GetReferencedAssemblies()` **không** liệt kê nó.

Đó là danh sách đúng cho những rule này: K8/K9 cấm A **gọi được** vào B, mà dependency compiler đã
xoá thì không gọi được. Đọc `.csproj` thay vào đó sẽ báo vi phạm cho một FB mượn đúng một hằng số.

**Project này tắt analyzer** (`UseNvmAnalyzers=false`). Nó chứa `EventWithForbiddenClock` — một event
cố tình sai mọi thứ, thứ mà NVM002/NVM003 sẽ chặn ngay lúc biên dịch. Đó cũng là điều đang được chứng
minh: type đó là type analyzer **chưa từng nhìn thấy**, và rule vẫn bắt được.

## Thêm rule mới

1. Viết rule, đặt tên `A{n}_<điều được bảo đảm>`.
2. Viết **đối chứng dương** `A{n}_Control_<phép kiểm thấy được gì>` — không có nó thì chưa xong.
3. Cho rule ăn một vi phạm thật, xác nhận đỏ, rồi hoàn nguyên.
4. Thêm dòng vào bảng đầu file này.
