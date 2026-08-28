# Nvm.Ingestion — the only M2 process with one foot in DMZ and one in IT.

FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY tools/ tools/
COPY src/ src/

RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet restore src/Workers/Nvm.Ingestion/Nvm.Ingestion.csproj
RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet publish src/Workers/Nvm.Ingestion/Nvm.Ingestion.csproj \
    -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-noble AS final
WORKDIR /app

# The CSV file drop checks equipment_path against the plant model, exactly as the MQTT path does (K3).
COPY --from=build /app .
COPY deploy/seed/ seed/

RUN mkdir -p /var/lib/nvm-ingestion && chown "$APP_UID" /var/lib/nvm-ingestion
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nvm.Ingestion.dll"]
