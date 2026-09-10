using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nvm.App.Execution;
using Nvm.Hosting;
using Nvm.PublicObjectModel;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
if (args.Contains("--health-probe", StringComparer.Ordinal))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        Environment.ExitCode = 1;
    }

    return;
}

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    DotEnvLoader.Load(builder.Environment.ContentRootPath);
}

builder.Configuration.AddEnvironmentVariables();
if (args.Contains("--prepare-poc", StringComparer.Ordinal))
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("The Equipment PoC fixture is only allowed in Development.");
    }

    await EquipmentPocSeed.PrepareAsync(builder.Configuration);
    return;
}

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    PomSchemaMigrator.Upgrade(builder.Configuration["NVM_POM:MigrationConnectionString"]
        ?? throw new InvalidOperationException("NVM_POM:MigrationConnectionString is required for --migrate."));
    return;
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddNvmPublicObjectModel(builder.Configuration, builder.Environment);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapNvmPublicObjectModel();
app.MapGet("/health/live", () => Results.Ok(new { Status = "Healthy" }));
app.MapGet("/health/ready", async (PomReadDbContext database, CancellationToken cancellationToken) =>
{
    try
    {
        // Kết nối được chưa đủ: schema và quyền SELECT cũng phải sẵn sàng.
        _ = await database.Equipment.AnyAsync(cancellationToken);
        return Results.Ok(new { Status = "Healthy" });
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
await app.RunAsync();
