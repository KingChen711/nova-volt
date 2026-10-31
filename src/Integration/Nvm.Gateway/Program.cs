using Nvm.Hosting;
using Nvm.Integration;

// Tiến trình gateway và hệ ngoài giả lập. `dotnet run -- external` chạy hệ giả lập; không tham số thì chạy gateway.
var envFile = DotEnvLoader.Load(Directory.GetCurrentDirectory());
var external = int.Parse(DotEnvLoader.Required("NVM_PORT_EXTERNAL"), System.Globalization.CultureInfo.InvariantCulture);
if (args.Contains("external"))
{
    await new ExternalSystems().Build($"http://localhost:{external}").RunAsync().ConfigureAwait(false);
    return;
}

var root = Path.GetDirectoryName(envFile) ?? Directory.GetCurrentDirectory();
var config = GatewayConfig.Load(Path.Combine(root, "deploy", "gateway", "gateway.json")) with
{
    ApiKey = DotEnvLoader.Required("NVM_GATEWAY_API_KEY"),
    KafkaBootstrap = $"localhost:{DotEnvLoader.Required("NVM_PORT_KAFKA")}",
    SmtpHost = "localhost",
    SmtpPort = int.Parse(DotEnvLoader.Required("NVM_PORT_SMTP"), System.Globalization.CultureInfo.InvariantCulture),
};
config = config with
{
    Out = [.. config.Out.Select(a => a with
    {
        Url = a.Url?.Replace("{external}", $"http://localhost:{external}", StringComparison.Ordinal),
        Directory = a.Directory is null ? null : Path.Combine(root, a.Directory),
    })],
    In = [.. config.In.Select(a => a with { Directory = a.Directory is null ? null : Path.Combine(root, a.Directory) })],
};

using var http = new HttpClient();
using var mom = new HttpMomClient(
    http,
    new Uri($"http://localhost:{Environment.GetEnvironmentVariable("NVM_PORT_MOM") ?? "5080"}/mom/{Environment.GetEnvironmentVariable("NVM_PLANT") ?? "NV1"}/Application/"),
    new Uri($"http://localhost:{DotEnvLoader.Required("NVM_PORT_KEYCLOAK")}/realms/novavolt/protocol/openid-connect/token"),
    "admin.nv1",
    DotEnvLoader.Required("NVM_DEMO_USER_PASSWORD"));
await GatewayApp.Build(config, mom, $"http://localhost:{DotEnvLoader.Required("NVM_PORT_GATEWAY")}").RunAsync().ConfigureAwait(false);
