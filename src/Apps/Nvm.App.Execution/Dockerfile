FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY tools/ tools/
COPY src/ src/
RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet publish src/Apps/Nvm.App.Execution/Nvm.App.Execution.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-noble AS final
WORKDIR /app
COPY --from=build /app .
COPY deploy/seed/factory-model.r3.json /app/seed/factory-model.r3.json
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nvm.App.Execution.dll"]
