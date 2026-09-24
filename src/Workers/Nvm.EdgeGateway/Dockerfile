# Nvm.EdgeGateway — MQTT subscriber in dmz-net, never a RabbitMQ publisher.

FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY tools/ tools/
COPY src/ src/

RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet restore src/Workers/Nvm.EdgeGateway/Nvm.EdgeGateway.csproj
RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet publish src/Workers/Nvm.EdgeGateway/Nvm.EdgeGateway.csproj \
    -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.3-noble AS final
WORKDIR /app

# Topic -> equipment path is checked against the plant model at the gateway (K3).
COPY deploy/seed/ seed/

# The 1.000-channel load topology, as a second catalog rather than a new revision of the first.
# NVM_EDGE__SeedDirectory picks between them; the demo stays on eight channels.
COPY deploy/seed-load/*.json seed-load/
COPY --from=build /app .

RUN mkdir -p /var/lib/nvm-edge-gateway && chown "$APP_UID" /var/lib/nvm-edge-gateway
USER $APP_UID
ENTRYPOINT ["dotnet", "Nvm.EdgeGateway.dll"]
