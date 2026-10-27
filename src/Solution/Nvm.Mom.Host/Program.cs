using Microsoft.Data.SqlClient;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Foundation.Web;
using Nvm.Hosting;
using Nvm.Mom.Host;

var builder = WebApplication.CreateBuilder(args);
DotEnvLoader.Load(builder.Environment.ContentRootPath);

var plant = Environment.GetEnvironmentVariable("NVM_PLANT") ?? "NV1";
var sqlPort = DotEnvLoader.Required("NVM_PORT_MSSQL");
var registry = ModelRegistry.Build(typeof(FoundationApp).Assembly);

if (args.Contains("--update-database"))
{
    // Bước deploy, chạy bằng quyền quản trị DB: bật READ_COMMITTED_SNAPSHOT cần quyền ALTER DATABASE.
    SchemaUpdater.UpdateDatabase(Sql(sqlPort, "sa", DotEnvLoader.Required("NVM_MSSQL_SA_PASSWORD")), registry);
    Console.WriteLine($"Update Database: plant {plant}, {registry.Entities.Count} entities, {registry.FacetTables.Count} facet tables.");
    return;
}

var connectionString = Sql(sqlPort, "nvm_app", DotEnvLoader.Required("NVM_MSSQL_APP_PASSWORD"));
var rabbitUri = $"amqp://{DotEnvLoader.Required("NVM_RABBITMQ_USER")}:{Uri.EscapeDataString(DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD"))}"
    + $"@127.0.0.1:{DotEnvLoader.Required("NVM_PORT_RABBITMQ")}/";
var webOrigin = $"http://localhost:{Environment.GetEnvironmentVariable("NVM_PORT_WEB") ?? "3100"}";
var options = new MomWebOptions
{
    Plant = plant,
    Authority = $"http://localhost:{DotEnvLoader.Required("NVM_PORT_KEYCLOAK")}/realms/novavolt",
};

var solution = new SolutionConfiguration { Plant = plant };
var time = TimeProvider.System;
builder.Services.AddSingleton(time);
builder.Services.AddSingleton<ITraceSink, LoggingTraceSink>();
builder.Services.AddSingleton(sp => new RabbitMqEventPublisher(rabbitUri, plant, connectionString, time, sp.GetRequiredService<ITraceSink>()));
builder.Services.AddSingleton(sp => new CommandExecutor(connectionString, registry, solution, time,
    sp.GetRequiredService<ITraceSink>(), sp.GetRequiredService<RabbitMqEventPublisher>()));
builder.Services.AddSingleton(sp => new SignalRuleEngine(sp.GetRequiredService<CommandExecutor>(), connectionString));
builder.Services.AddSingleton(sp => new EventWorker(sp.GetRequiredService<CommandExecutor>(), sp.GetRequiredService<SignalRuleEngine>(), rabbitUri));
builder.Services.AddHostedService<FoundationBackgroundService>();
builder.Services.AddMomAuthentication(options);
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .WithOrigins(webOrigin).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Correlation-Id")));
builder.WebHost.UseUrls($"http://localhost:{Environment.GetEnvironmentVariable("NVM_PORT_MOM") ?? "5080"}");

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new { plant, status = "ok" }));
app.MapMom(app.Services.GetRequiredService<CommandExecutor>(), options);
app.Run();

static string Sql(string port, string user, string password) => new SqlConnectionStringBuilder
{
    DataSource = $"127.0.0.1,{port}",
    InitialCatalog = "NovaVolt",
    UserID = user,
    Password = password,
    TrustServerCertificate = true,
}.ConnectionString;
