using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Nvm.Bus;
using Nvm.Bus.Outbox;
using Nvm.CommandStore;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.Grading.Commands;
using Nvm.Grading.Hosting;
using Nvm.Host.Infrastructure;
using Nvm.Hosting;
using Nvm.Kernel;
using Nvm.Material.Commands;
using Nvm.Material.Hosting;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Hosting;
using Nvm.PublicObjectModel;
using Nvm.Quality.Hosting;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Hosting;
using Serilog;
using Serilog.Events;

const string logTemplate =
    "[{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

// Định dạng tất định (deterministic), không phụ thuộc locale của máy.
// Cách này thay thế cho InvariantGlobalization=true, vốn sẽ tắt ICU và khiến
// TimeZoneInfo từ chối các IANA id như "Europe/Berlin". Xem ADR-020.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Bootstrap logger: bắt các lỗi xảy ra trước khi host được build xong,
// đúng lúc mọi sai sót cấu hình lộ ra.
// formatProvider được khai rõ ràng ở mọi sink: CA1305 chính là thứ ép buộc tính tất định
// mà lẽ ra InvariantGlobalization đã cho ta.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: logTemplate, formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: logTemplate, formatProvider: CultureInfo.InvariantCulture));

    // Một nguồn sự thật duy nhất cho port và credential: cùng file .env mà docker-compose đọc.
    // Chỉ dùng ở Development — xem DotEnvLoader.
    if (builder.Environment.IsDevelopment())
    {
        var envFile = DotEnvLoader.Load(builder.Environment.ContentRootPath);
        Log.Information("Loaded environment from {EnvFile}", envFile ?? "(none found)");
    }

    builder.Configuration.AddEnvironmentVariables();
    builder.Services.AddNvmKernel(typeof(ActivateFactoryModelRevisionCommand).Assembly, typeof(RecordDataCollectionCommand).Assembly,
        typeof(SerializeUnitCommand).Assembly, typeof(ConsumeMaterialCommand).Assembly, typeof(GradeUnitCommand).Assembly);
    builder.Services.AddNvmCommandStore(builder.Configuration, builder.Environment);
    builder.Services.AddNvmTraceability(builder.Configuration);
    builder.Services.AddNvmQuality();
    builder.Services.AddNvmMaterial();
    builder.Services.AddNvmGrading();
    builder.Services.AddNvmPublicObjectModel(builder.Configuration, builder.Environment);
    builder.Services.AddNvmProductionExecutionAdapters(builder.Configuration);

    // Đồng hồ duy nhất được chấp nhận trong codebase. AGENTS.md K1 cấm DateTime.UtcNow
    // để những saga kéo dài cả ngày vẫn test được bằng FakeTimeProvider.
    builder.Services.AddSingleton(TimeProvider.System);

    // Manufacturing Service Bus. Credential lấy từ cùng file .env mà docker-compose đọc —
    // một nguồn sự thật duy nhất, không có file thứ hai giữ cùng một password (M0/C10.2).
    // Chưa có consumer nào ở đây: host này chỉ publish, còn probe worker mới consume.
    builder.Services.AddNvmBus(bus =>
    {
        bus.Host = builder.Configuration["NVM_RABBITMQ_HOST"] ?? "localhost";
        bus.Port = ushort.Parse(DotEnvLoader.Required("NVM_PORT_RABBITMQ"), CultureInfo.InvariantCulture);
        bus.Username = DotEnvLoader.Required("NVM_RABBITMQ_USER");
        bus.Password = DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD");
        // Đặt tên cho deployable này trong mọi CloudEvents source nó publish ra:
        // urn:novavolt:nv1:host-all. Chế độ dev chạy mọi App trong cùng một process (scope.md §5.3).
        bus.ApplicationName = "host-all";
    });
    builder.Services.AddNvmSqlEventOutbox();

    // Command pipeline cộng với Functional Block đầu tiên. Kernel được cho biết chính xác những
    // assembly nào cần scan, thay vì scan mọi thứ đã load: một Functional Block chưa từng tự công bố
    // mình thì không nên được wire up chỉ vì tình cờ nằm trong output directory.
    builder.Services.AddNvmFactoryModel(SeedDirectoryLocator.Locate(builder.Environment.ContentRootPath));

    // Production calendar, đọc time zone của từng plant từ model ở trên thay vì từ một bảng tra cứu
    // thứ hai. Chỉ được đăng ký ở đây và duy nhất ở đây — M4 là màn hình đầu tiên hỏi nó
    // "ca này đã sản xuất được gì".
    builder.Services.AddNvmProductionCalendar();

    builder.Services.AddDependencyHealthChecks();
    builder.Services.AddHealthChecks().AddCheck<CommandStoreHealthCheck>("command-store", tags: [HealthTags.Ready]);
    builder.Services.AddHealthChecks().AddCheck<TraceabilityStoreHealthCheck>("traceability-store", tags: [HealthTags.Ready]);

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
        options.GetLevel = (httpContext, _, exception) => exception is not null
            ? LogEventLevel.Error
            : httpContext.Response.StatusCode >= 500
                ? LogEventLevel.Error
                // Health probe chạy vài giây một lần; log chúng ở mức Information
                // sẽ chôn vùi mọi thứ khác.
                : httpContext.Request.Path.StartsWithSegments("/health")
                    ? LogEventLevel.Verbose
                    : LogEventLevel.Information);

    app.MapGet("/", (TimeProvider clock, IHostEnvironment environment) => new
    {
        Name = "NovaVolt MES — Host.All",
        Version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown",
        Environment = environment.EnvironmentName,
        UtcNow = clock.GetUtcNow(),
    });

    // Liveness và readiness cố ý được tách riêng.
    //   live  = "process vẫn còn sống, đừng restart tôi"
    //   ready = "dependency của tôi với tới được, hãy gửi traffic sang"
    // Gộp chúng lại sẽ khiến orchestrator giết một process khoẻ mạnh mỗi khi
    // database chỉ đang chậm tạm thời.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains(HealthTags.Live),
        ResponseWriter = HealthReportWriter.Write,
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains(HealthTags.Ready),
        ResponseWriter = HealthReportWriter.Write,
    });

    // Cùng quy tắc như DotEnvLoader: một tiện ích cho laptop mà không được phép tồn tại ở bất cứ đâu
    // khác. Các endpoint này publish event theo yêu cầu, là một công cụ hữu ích khi ở development
    // nhưng là một đường ghi không được canh gác vào event stream của nhà máy ở mọi môi trường khác.
    if (app.Environment.IsDevelopment())
    {
        app.MapDevBusEndpoints();
    }

    app.UseAuthentication();
    app.UseAuthorization();
    app.MapNvmPublicObjectModel();
    app.MapNvmProductionExecution();
    app.MapNvmTraceability();
    app.MapNvmMaterial();
    app.MapNvmGrading();
    app.Run();
    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Host terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
