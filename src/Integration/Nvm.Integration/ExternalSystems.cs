using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nvm.Integration;

/// <summary>Tin một hệ giả lập đã nhận.</summary>
public sealed record ReceivedMessage(string System, string Path, string Content, DateTimeOffset On);

/// <summary>
/// Hệ ngoài giả lập: ERP, cổng nhà cung cấp, hệ của khách, hệ chất lượng, WMS, dịch vụ đặt bù. Ghi lại mọi tin nhận được; hệ của khách cấp số xác
/// nhận, có thể cho chết để lab; cổng nhà cung cấp trả danh sách kanban; WMS gọi ngược gateway khi được yêu cầu.
/// </summary>
public sealed class ExternalSystems
{
    private readonly ConcurrentQueue<ReceivedMessage> _received = new();
    private int _confirmation;

    public IReadOnlyCollection<ReceivedMessage> Received => _received;

    /// <summary>Tắt thì hệ của khách trả 503, như hệ chết.</summary>
    public bool CustomerUp { get; set; } = true;

    /// <summary>Danh sách kanban cổng nhà cung cấp trả về khi MOM hỏi.</summary>
    public string KanbanList { get; set; } =
        """[{"part":"NV-RM-CASE-A","dock":"L1","supplier":"SUP-1","kanban":"KB-CASE-A","quantity":300,"effectiveFrom":"2026-01-01"}]""";

    public IEnumerable<ReceivedMessage> Of(string system) => _received.Where(m => m.System == system);

    public WebApplication Build(string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        Record(app, "/erp/production", "ERP");
        Record(app, "/erp/consumption", "ERP");
        Record(app, "/erp/ack", "ERP");
        Record(app, "/supplier/part-address", "SupplierPortal");
        Record(app, "/supplier/ack", "SupplierPortal");
        Record(app, "/quality/ack", "QualityTrace");
        Record(app, "/wms/picklist", "WMS");
        Record(app, "/reorder", "ReorderSvc");
        app.MapPost("/supplier/parts", async (HttpContext http) =>
        {
            await Keep(http, "SupplierPortal").ConfigureAwait(false);
            return Results.Text(KanbanList, "application/json");
        });
        app.MapPost("/customer/skid-build", async (HttpContext http) =>
        {
            return await Customer(http, () => new { received = true }).ConfigureAwait(false);
        });
        app.MapPost("/customer/ship-load", async (HttpContext http) =>
        {
            return await Customer(http, () =>
            {
                var next = Interlocked.Increment(ref _confirmation);
                return new { confirmationNo = $"CF-{next:000000}", tripNo = $"TP-{next:000000}" };
            }).ConfigureAwait(false);
        });
        app.MapPost("/admin/customer/{state}", (string state) =>
        {
            CustomerUp = state == "up";
            return Results.Ok(new { CustomerUp });
        });
        app.MapGet("/received/{system}", (string system) => Results.Json(Of(system)));
        return app;
    }

    private void Record(WebApplication app, string path, string system) =>
        app.MapPost(path, async (HttpContext http) =>
        {
            await Keep(http, system).ConfigureAwait(false);
            return Results.Accepted();
        });

    /// <summary>Chỗ map phải viết lambda có khối và <c>return</c>: lambda async một biểu thức sẽ khớp RequestDelegate và mất kết quả.</summary>
    private async Task<IResult> Customer(HttpContext http, Func<object> reply)
    {
        if (!CustomerUp)
        {
            return Results.StatusCode(503);
        }

        await Keep(http, "CustomerShipSys").ConfigureAwait(false);
        return Results.Text(JsonSerializer.Serialize(reply()), "application/json");
    }

    private async Task Keep(HttpContext http, string system)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        _received.Enqueue(new ReceivedMessage(system, http.Request.Path, await reader.ReadToEndAsync(http.RequestAborted).ConfigureAwait(false), TimeProvider.System.GetUtcNow()));
    }
}
