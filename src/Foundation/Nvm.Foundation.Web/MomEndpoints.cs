using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nvm.Foundation.Runtime;

namespace Nvm.Foundation.Web;

/// <summary>
/// OData theo app: <c>/mom/{plant}/Application/{app}/odata/...</c>. GET đọc entity set, một bản ghi theo Id hoặc reading
/// function; POST gọi command theo tên công bố. App chỉ thấy thứ nó import vào POM.
/// </summary>
public static partial class MomEndpoints
{
    private static readonly JsonSerializerOptions RowJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static RouteGroupBuilder MapMom(this IEndpointRouteBuilder endpoints, CommandExecutor executor, MomWebOptions options)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        var group = endpoints.MapGroup("/mom/{plant}/Application/{app}/odata").RequireAuthorization();

        group.MapGet("/{resource}", (HttpContext http, string plant, string app, string resource) =>
            ReadAsync(http, executor, options, plant, app, resource));

        group.MapPost("/{command}", async (HttpContext http, string plant, string app, string command) =>
        {
            if (Refuse(executor, options, plant, app) is { } refused)
            {
                return refused;
            }

            JsonElement body = default;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
                body = document.RootElement.Clone();
            }

            var result = executor.Execute(app, command, body, MomAuthentication.Caller(http.User));
            http.Response.Headers["X-Correlation-Id"] = result.CorrelationId.ToString();
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });

        return group;
    }

    private static async Task<IResult> ReadAsync(HttpContext http, CommandExecutor executor, MomWebOptions options, string plant, string app, string resource)
    {
        if (Refuse(executor, options, plant, app) is { } refused)
        {
            return refused;
        }

        var match = ResourcePattern().Match(resource);
        if (!match.Success)
        {
            return Error(400, "InvalidQuery", $"'{resource}' is not an entity set or function call.");
        }

        var name = match.Groups["name"].Value;
        var args = match.Groups["args"];
        var registry = executor.Registry;
        var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);

        using var session = executor.OpenRead(MomAuthentication.Caller(http.User));
        IQueryable source;
        var single = false;
        if (registry.EntitySetIn(app, name) is { } set && (!args.Success || Guid.TryParse(args.Value.Trim('\''), out _)))
        {
            source = session.Set(set);
            if (args.Success)
            {
                // Đọc theo khóa: Id là Guid, không nháy.
                query["$filter"] = $"Id eq {args.Value.Trim('\'')}";
                single = true;
            }
        }
        else if (registry.ReadingFunctionByPublicName(app, name) is { } function)
        {
            try
            {
                source = session.Invoke(function, FunctionArguments(args.Success ? args.Value : string.Empty));
            }
#pragma warning disable CA1031 // lỗi do code của reading function ném lúc gọi (chưa ghi gì ra response): trả nguyên câu lỗi
            catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
            {
                return Error(500, "ReadingFunctionFailed", e.Message);
            }
        }
        else
        {
            return Error(404, "NotFound", $"App '{app}' publishes no entity set or reading function named '{name}'.");
        }

        IQueryable rows;
        IQueryable filtered;
        try
        {
            (rows, filtered) = ODataQuery.Apply(source, query);
        }
        catch (ODataQueryException e)
        {
            return Error(400, "InvalidQuery", e.Message);
        }

        var context = $"{http.Request.Scheme}://{http.Request.Host}/mom/{plant}/Application/{app}/odata/$metadata#{name}";
        if (single)
        {
            var item = rows.Cast<object>().FirstOrDefault();
            if (item is null)
            {
                return Error(404, "NotFound", $"{name}({args.Value}) does not exist.");
            }

            var node = JsonSerializer.SerializeToNode(item, item.GetType(), RowJson)!.AsObject();
            node.Insert(0, "@odata.context", context + "/$entity");
            return Results.Json(node);
        }

        long? count = query.TryGetValue("$count", out var c) && c == "true" ? filtered.Cast<object>().LongCount() : null;
        await StreamAsync(http, rows, context, count).ConfigureAwait(false);
        return Results.Empty;
    }

    /// <summary>
    /// Ghi đầu response trước rồi mới đọc DB: query trì hoãn chạy trong lúc ghi. Lỗi giữa chừng thì cắt kết nối; người
    /// gọi nhận HTTP 200 với thân dở (lab L15: view của reading model chưa cài).
    /// </summary>
    private static async Task StreamAsync(HttpContext http, IQueryable rows, string context, long? count)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = "application/json; odata.metadata=minimal";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("@odata.context", context);
        if (count is not null)
        {
            writer.WriteNumber("@odata.count", count.Value);
        }

        writer.WritePropertyName("value");
        writer.WriteStartArray();
        await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);

        try
        {
            var written = 0;
            foreach (var item in (IEnumerable)rows)
            {
                // Không gọi Serialize(writer, …): hàm đó Flush đồng bộ xuống stream của response, mà ASP.NET Core cấm ghi đồng bộ.
                writer.WriteRawValue(JsonSerializer.SerializeToUtf8Bytes(item, item.GetType(), RowJson), skipInputValidation: true);
                if (++written % 100 == 0)
                {
                    await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                }
            }
        }
#pragma warning disable CA1031 // đầu response đã gửi: không còn đổi được mã HTTP, chỉ còn cách cắt kết nối
        catch (Exception)
#pragma warning restore CA1031
        {
            await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
            http.Abort();
            return;
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    private static IResult? Refuse(CommandExecutor executor, MomWebOptions options, string plant, string app)
    {
        if (!string.Equals(plant, options.Plant, StringComparison.Ordinal))
        {
            return Error(404, "PlantNotFound", $"This service serves plant '{options.Plant}', not '{plant}'.");
        }

        return executor.Registry.HasApp(app) ? null : Error(404, "AppNotFound", $"App '{app}' does not exist.");
    }

    /// <summary><c>A='x',B=5</c> thành đối tượng JSON cho tham số của reading function.</summary>
    private static JsonElement FunctionArguments(string text)
    {
        var node = new JsonObject();
        foreach (Match match in ArgumentPattern().Matches(text))
        {
            var raw = match.Groups["value"].Value;
            node[match.Groups["name"].Value] = raw.StartsWith('\'')
                ? JsonValue.Create(raw[1..^1].Replace("''", "'", StringComparison.Ordinal))
                : decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : bool.TryParse(raw, out var flag) ? JsonValue.Create(flag) : JsonValue.Create(raw);
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(\((?<args>.*)\))?$")]
    private static partial Regex ResourcePattern();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>'(?:[^']|'')*'|[^,]+)")]
    private static partial Regex ArgumentPattern();
}
