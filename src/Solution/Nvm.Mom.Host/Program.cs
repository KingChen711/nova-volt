using Microsoft.Data.SqlClient;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Foundation.Web;
using Nvm.Hosting;
using Nvm.Mom.Host;
using Nvm.Project;
using Nvm.Solution;
using Nvm.Solution.Suites;

var builder = WebApplication.CreateBuilder(args);
var envFile = DotEnvLoader.Load(builder.Environment.ContentRootPath);

var plant = Environment.GetEnvironmentVariable("NVM_PLANT") ?? "NV1";
var sqlPort = DotEnvLoader.Required("NVM_PORT_MSSQL");
var registry = SolutionManifest.BuildRegistry();
var solution = SolutionManifest.Configure(plant);

// LineDB: MOM vào bằng role chỉ gọi được hàm của schema trung gian (K11). Thiếu mật khẩu thì không khai endpoint: handler tin
// máy ghi trace lỗi, lệnh đọc tin trả lỗi.
var lineDbMomPassword = Environment.GetEnvironmentVariable("NVM_LINEDB_MOM_PASSWORD");
if (!string.IsNullOrEmpty(lineDbMomPassword))
{
    solution.Endpoints[NvEndpoints.LineDb] = LineDb("nvm_mom", lineDbMomPassword);
}

if (args.Contains("--update-database"))
{
    // Bước deploy, chạy bằng quyền quản trị DB: bật READ_COMMITTED_SNAPSHOT cần quyền ALTER DATABASE.
    SchemaUpdater.UpdateDatabase(Sql(sqlPort, "sa", DotEnvLoader.Required("NVM_MSSQL_SA_PASSWORD")), registry);
    Console.WriteLine($"Update Database: plant {plant}, {registry.Entities.Count} entities, {registry.FacetTables.Count} facet tables.");
    return;
}

if (args.Contains("--install-views"))
{
    // Bước deploy riêng sau Update Database: view của reading model là phần cài tay, Update Database không tạo (lab L15).
    var views = ReadingViews.Install(Sql(sqlPort, "sa", DotEnvLoader.Required("NVM_MSSQL_SA_PASSWORD")), registry);
    Console.WriteLine($"Views: {string.Join(", ", views)}.");
    return;
}

var connectionString = Sql(sqlPort, "nvm_app", DotEnvLoader.Required("NVM_MSSQL_APP_PASSWORD"));

if (args.Contains("--set-sql-connection-key"))
{
    // Bước cài tay của môi trường: key giữ chuỗi kết nối mà code thành phẩm dùng gọi procedure. Giá trị lấy từ .env, không
    // nằm trong suite hay repo (K13); thiếu bước này thì màn và lệnh dùng procedure báo thiếu key (lab L18).
    var keyExecutor = new CommandExecutor(connectionString, registry, solution, TimeProvider.System, new InMemoryTraceSink(), new InMemoryEventPublisher());
    var set = keyExecutor.Execute(SolutionManifest.FoundationAppName,
        new SetConfigurationKey { NId = NvKeys.SqlConnection, Val = connectionString, Type = "String", Category = "FinishedGoods" }, CallerIdentity.System);
    Console.WriteLine($"{NvKeys.SqlConnection}: HTTP {set.StatusCode}.");
    Environment.ExitCode = set.StatusCode == 200 ? 0 : 1;
    return;
}

if (args.Contains("--load-master-data") || args.Contains("--enable-line-polling") || args.Contains("--load-quality-config"))
{
    // Bước deploy sau Update Database: chạy suite theo masterDataOrder, bỏ qua suite đã có trong lịch sử. --enable-line-polling
    // là bước riêng của môi trường dùng đường poll cho tin từ xưởng (suites/line-polling), có lịch sử riêng.
    // --load-quality-config nạp cấu hình hủy theo ca (suites/quality), chạy sau master data vì cần PS.
    var root = Path.GetDirectoryName(envFile ?? throw new InvalidOperationException(".env not found above the host."))!;
    var step = args.Contains("--enable-line-polling") ? "line-polling" : args.Contains("--load-quality-config") ? "quality" : null;
    var suites = Path.Combine(root, "suites", step ?? string.Empty);
    var historyName = step switch
    {
        "line-polling" => $"LinePollingHistory.{plant}.txt",
        "quality" => $"QualityHistory.{plant}.txt",
        _ => MasterDataDeployment.HistoryFileName.Replace(".txt", $".{plant}.txt", StringComparison.Ordinal),
    };
    var history = Path.Combine(root, "suites", ".history", historyName);
    Directory.CreateDirectory(Path.GetDirectoryName(history)!);
    var executor = new CommandExecutor(connectionString, registry, solution, TimeProvider.System, new InMemoryTraceSink(), new InMemoryEventPublisher());
    var report = MasterDataDeployment.Run(suites, new InProcessTarget(executor, connectionString, CallerIdentity.System), history);
    foreach (var suite in report)
    {
        Console.WriteLine($"{suite.Status,-8} {suite.App}/{suite.Suite}");
        foreach (var line in suite.Log.Where(l => l.Contains("Failed", StringComparison.Ordinal)))
        {
            Console.WriteLine("         " + line);
        }
    }

    Environment.ExitCode = report.Any(s => s.Status == SuiteStatus.Failed) ? 1 : 0;
    return;
}

if (args.Contains("--install-linedb"))
{
    // Cài schema LineDB (phía xưởng) bằng owner của database; tạo role MOM nvm_mom. Biến thể chỗ lưu trạng thái và môi trường
    // lấy từ NVM_LINEDB_STATE (table | columns) và NVM_ENVIRONMENT (qa | stg | prod).
    var storage = string.Equals(Environment.GetEnvironmentVariable("NVM_LINEDB_STATE"), "columns", StringComparison.OrdinalIgnoreCase)
        ? Nvm.Shopfloor.LineDb.LineStateStorage.Columns
        : Nvm.Shopfloor.LineDb.LineStateStorage.Table;
    var environment = Environment.GetEnvironmentVariable("NVM_ENVIRONMENT") ?? "qa";
    Nvm.Shopfloor.LineDb.LineDbInstaller.Install(
        LineDb(DotEnvLoader.Required("NVM_LINEDB_USER"), DotEnvLoader.Required("NVM_LINEDB_PASSWORD")),
        new Nvm.Shopfloor.LineDb.LineDbOptions { Storage = storage, Environment = environment, MomPassword = lineDbMomPassword },
        reset: args.Contains("--reset"));
    Console.WriteLine($"LineDB: state {storage}, environment {environment}, role nvm_mom {(string.IsNullOrEmpty(lineDbMomPassword) ? "not created" : "ready")}.");
    return;
}

var rabbitUri = $"amqp://{DotEnvLoader.Required("NVM_RABBITMQ_USER")}:{Uri.EscapeDataString(DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD"))}"
    + $"@127.0.0.1:{DotEnvLoader.Required("NVM_PORT_RABBITMQ")}/";
var webOrigin = $"http://localhost:{Environment.GetEnvironmentVariable("NVM_PORT_WEB") ?? "3100"}";
var options = new MomWebOptions
{
    Plant = plant,
    Authority = $"http://localhost:{DotEnvLoader.Required("NVM_PORT_KEYCLOAK")}/realms/novavolt",
};

var time = TimeProvider.System;
builder.Services.AddSingleton(time);
builder.Services.AddSingleton<ITraceSink, LoggingTraceSink>();
builder.Services.AddSingleton(sp => new RabbitMqEventPublisher(rabbitUri, plant, connectionString, time, sp.GetRequiredService<ITraceSink>()));
builder.Services.AddSingleton(sp => new CommandExecutor(connectionString, registry, solution, time,
    sp.GetRequiredService<ITraceSink>(), sp.GetRequiredService<RabbitMqEventPublisher>()));
builder.Services.AddSingleton(sp => new SignalRuleEngine(sp.GetRequiredService<CommandExecutor>(), connectionString));
builder.Services.AddSingleton(sp => new EventWorker(sp.GetRequiredService<CommandExecutor>(), sp.GetRequiredService<SignalRuleEngine>(), rabbitUri));
builder.Services.AddHostedService<FoundationBackgroundService>();
if (solution.Endpoints.TryGetValue(NvEndpoints.LineDb, out var lineDbEndpoint))
{
    // Bên nghe notify của đường relay. Đường nào đang dùng là configuration key Nv.LineTransfer; nghe thừa thì vô hại.
    builder.Services.AddHostedService(sp => new LineRelayListenerService(lineDbEndpoint, sp.GetRequiredService<CommandExecutor>(),
        sp.GetRequiredService<ILogger<LineRelayListenerService>>()));
}
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

static string LineDb(string user, string password) => new Npgsql.NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1",
    Port = int.Parse(DotEnvLoader.Required("NVM_PORT_LINEDB"), System.Globalization.CultureInfo.InvariantCulture),
    Database = DotEnvLoader.Required("NVM_LINEDB_DB"),
    Username = user,
    Password = password,
}.ConnectionString;

static string Sql(string port, string user, string password) => new SqlConnectionStringBuilder
{
    DataSource = $"127.0.0.1,{port}",
    InitialCatalog = "NovaVolt",
    UserID = user,
    Password = password,
    TrustServerCertificate = true,
}.ConnectionString;
