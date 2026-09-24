# Template Functional Block

Chạy từ root repo:

```powershell
dotnet new install ./tools/templates/functional-block
dotnet new nvm-fb -n Example -o ./src/FunctionalBlocks/Example
dotnet sln NovaVolt.Mes.slnx add ./src/FunctionalBlocks/Example/Nvm.Example.csproj
dotnet build ./src/FunctionalBlocks/Example/Nvm.Example.csproj
```

`Example` là tên minh hoạ. Template sinh các thư mục `Entities`, `Facets`, `Commands`,
`Handlers`, `Events`, `Migrations`, `PublicObjectModel` và marker `ExampleModule`.
Project nhận cấu hình .NET/analyzer từ repo, chỉ reference Contracts và Kernel.
Host thêm assembly vào `AddNvmKernel(typeof(ExampleModule).Assembly)` khi block có handler.

Đặt event dùng qua bus trong `Nvm.Contracts/Events/<Block>`, không reference FB khác.
Adapter SQL, HTTP và bus thuộc project hosting riêng; thư mục trong template không cho phép
domain phụ thuộc infrastructure. Template này là công cụ riêng của NovaVolt, không phải
artifact hoặc API của Siemens.
