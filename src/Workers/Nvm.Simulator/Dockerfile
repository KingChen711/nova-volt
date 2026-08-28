# Nvm.Simulator — nguồn dữ liệu thiết bị của M2.
#
# Build context là GỐC repo, không phải thư mục này. Central package management đặt
# Directory.Packages.props ở gốc và Directory.Build.targets nối analyzer từ tools/ vào
# mọi project, nên build từ thư mục con thì restore thiếu cả hai và hỏng ở chỗ khó đọc.
#
#     docker compose --profile sim build simulator
#
# Tag pin cứng, cùng lý do đã ghi cho mọi image trong docker-compose.yml: build hôm nay
# và build sáu tháng nữa phải cho cùng kết quả.

FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src

# Manifest trước, mã nguồn sau. Đổi một dòng code thì layer restore vẫn dùng lại được,
# và restore là bước duy nhất trong Dockerfile này cần mạng.
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY tools/ tools/
COPY src/ src/

RUN dotnet restore src/Workers/Nvm.Simulator/Nvm.Simulator.csproj

# TreatWarningsAsErrors bật cho toàn repo, nên build ở đây nghiêm đúng bằng build ở máy.
# Đó là chủ đích: một image build được trong khi `make ci` đỏ sẽ che mất lỗi thật.
RUN dotnet publish src/Workers/Nvm.Simulator/Nvm.Simulator.csproj \
    -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.3-noble AS final
WORKDIR /app

# Danh sách kênh đến từ model của nhà máy, không từ hằng số trong code (K3). Một topic
# trỏ tới thiết bị không có trong model sẽ bị ingestion từ chối, và một simulator sinh
# ra toàn message bị từ chối thì không đo được gì.
COPY deploy/seed/ seed/

COPY --from=build /app .

# Báo cáo run — vế trái của phép đối chiếu D1 — nằm ngoài /app vì /app thuộc root và
# process không chạy bằng root. Thư mục này được compose gắn volume.
RUN mkdir -p /var/lib/nvm-simulator && chown "$APP_UID" /var/lib/nvm-simulator
ENV NVM_SIM__ReportPath=/var/lib/nvm-simulator/simulator-run.json

# APP_UID (1654) do image runtime khai. Một simulator không cần quyền root, và ở ot-net
# thì nó là process duy nhất đứng cạnh thiết bị thật.
USER $APP_UID

ENTRYPOINT ["dotnet", "Nvm.Simulator.dll"]
