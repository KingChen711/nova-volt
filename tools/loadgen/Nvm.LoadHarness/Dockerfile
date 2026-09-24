# Nvm.LoadHarness — đo N1/N2, chạy TRONG ot-net như một thiết bị thật (scope.md §13.1).

FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY tools/ tools/
COPY src/ src/

RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet restore tools/loadgen/Nvm.LoadHarness/Nvm.LoadHarness.csproj
RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet publish tools/loadgen/Nvm.LoadHarness/Nvm.LoadHarness.csproj \
    -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.3-noble AS final
WORKDIR /app
COPY --from=build /app .

# Danh sách kênh lấy từ model của nhà máy, không bịa (K3).
COPY deploy/seed/ seed/

# Topology 1.000 kênh của bài test/load. Gateway phải trỏ vào CÙNG thư mục này trong cùng lần
# chạy, nếu không nó sẽ từ chối mọi topic mà harness phát ra — đúng theo K3.
COPY deploy/seed-load/*.json seed-load/

USER $APP_UID
ENTRYPOINT ["dotnet", "Nvm.LoadHarness.dll"]
