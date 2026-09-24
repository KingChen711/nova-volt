FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY tools/ tools/
COPY src/ src/
RUN --mount=type=cache,id=novavolt-nuget,target=/root/.nuget/packages \
    dotnet publish src/Workers/Nvm.ProjectionWorker/Nvm.ProjectionWorker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.3-noble AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nvm.ProjectionWorker.dll"]
