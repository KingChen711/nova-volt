using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

// NovaERP stub. Dữ liệu bẩn lấy đúng các ca của scope §7.6: bốn mã cho một vật liệu, mã sản phẩm có khoảng trắng thừa,
// vật liệu chưa tồn tại, gram thay vì kg, lịch gửi lặp, file sai schema.
if (args is ["generate", var directory])
{
    Directory.CreateDirectory(directory);
    var files = new (string Name, string Content)[]
    {
        ("001-ps-0834.xml", Schedule("PS-2026-0834", "WO-2026-0042", "NV-P120-NMC", "MAT-0009812", "480", "kg")),
        ("002-ps-0835.xml", Schedule("PS-2026-0835", "WO-2026-0043", "NV-P120-NMC ", "9812", "480", "kg")),
        ("003-ps-0836.xml", Schedule("PS-2026-0836", "WO-2026-0044", "NV-P120-NMC", "MAT9812", "480", "kg")),
        ("004-ps-0837.xml", Schedule("PS-2026-0837", "WO-2026-0045", "NV-P120-NMC", "NMC811-CAM", "480", "kg")),
        ("005-ps-0838.xml", Schedule("PS-2026-0838", "WO-2026-0046", "NV-P120-NMC", "MAT-0009999", "12", "kg")),
        ("006-ps-0839.xml", Schedule("PS-2026-0839", "WO-2026-0047", "NV-P120-NMC", "MAT-0009812", "480000", "g")),
        ("007-ps-0834-resend.xml", Schedule("PS-2026-0834", "WO-2026-0042", "NV-P120-NMC", "MAT-0009812", "480", "kg")),
        ("008-ps-0840-bad.xml", Schedule("PS-2026-0840", "WO-2026-0048", "NV-P120-NMC", "MAT-0009812", "bốn trăm", "kg")),
    };
    foreach (var (name, content) in files)
    {
        // Ghi file tạm rồi đổi tên: gateway không bao giờ thấy file đang ghi dở.
        var temporary = Path.Combine(directory, name + ".part");
        await File.WriteAllTextAsync(temporary, content, Encoding.UTF8);
        File.Move(temporary, Path.Combine(directory, name), overwrite: true);
    }
    await Console.Out.WriteLineAsync($"Wrote {files.Length} B2MML files to {directory}");
    return 0;
}

if (args is ["serve", var url, var output])
{
    var builder = WebApplication.CreateBuilder();
    var app = builder.Build();
    var seen = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    var gate = new SemaphoreSlim(1, 1);
    app.MapPost("/api/backflush", async (HttpContext context) =>
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length == 0)
        { return Results.BadRequest("Idempotency-Key is required."); }
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        if (!seen.TryAdd(key, true))
        { return Results.Ok(new { duplicate = true }); }
        await gate.WaitAsync(context.RequestAborted);
        try
        { await File.AppendAllTextAsync(output, body.ReplaceLineEndings(" ") + Environment.NewLine, context.RequestAborted); }
        finally
        { gate.Release(); }
        return Results.Ok(new { duplicate = false });
    });
    app.Urls.Add(url);
    await app.RunAsync();
    return 0;
}

await Console.Error.WriteLineAsync("Usage: Nvm.NovaErp generate <inbound-dir> | serve <url> <out.jsonl>");
return 2;

static string Schedule(string scheduleId, string workOrderId, string product, string material, string quantity, string uom) =>
    string.Create(CultureInfo.InvariantCulture, $"""
    <?xml version="1.0" encoding="utf-8"?>
    <ProductionSchedule xmlns="http://www.mesa.org/xml/B2MML-V0600">
      <ID>{scheduleId}</ID>
      <ProductionRequest>
        <ID>{workOrderId}</ID>
        <ProductProductionRule><ID>{product}</ID></ProductProductionRule>
        <EarliestStartTime>2026-08-25T06:00:00+07:00</EarliestStartTime>
        <SegmentRequirement>
          <MaterialRequirement>
            <MaterialDefinitionID>{material}</MaterialDefinitionID>
            <Quantity><QuantityString>{quantity}</QuantityString><UnitOfMeasure>{uom}</UnitOfMeasure></Quantity>
          </MaterialRequirement>
        </SegmentRequirement>
      </ProductionRequest>
    </ProductionSchedule>
    """);
