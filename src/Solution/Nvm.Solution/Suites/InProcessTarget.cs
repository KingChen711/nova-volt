using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Nvm.Foundation;
using Nvm.Foundation.Runtime;
using Nvm.Foundation.Web;

// Câu SQL của step do người viết suite đặt, chạy trên DB MOM bằng quyền của host: như step SQL của Tester Tool.
#pragma warning disable CA2100

namespace Nvm.Solution.Suites;

/// <summary>
/// Chạy suite trong tiến trình, qua cùng đường với service layer: command theo tên công bố, đọc entity set và reading
/// function bằng bộ OData của nền. Dùng khi deploy (nạp master data) và trong test.
/// </summary>
public sealed class InProcessTarget(CommandExecutor executor, string connectionString, CallerIdentity? caller = null) : ISuiteTarget
{
    private static readonly JsonSerializerOptions RowJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public StepOutcome Command(string app, string command, JsonObject parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (executor.Registry.HasApp(app) && executor.Registry.CommandByPublicName(app, command) is { } model)
        {
            parameters = Typed(parameters, model.Type);
        }

        using var document = JsonDocument.Parse(parameters.ToJsonString());
        var result = executor.Execute(app, command, document.RootElement.Clone(), caller);
        var message = result.StatusCode == 200 ? null : $"HTTP {result.StatusCode}: {result.Body.ToJsonString()}";
        return new StepOutcome(result.StatusCode == 200, result.Body, message);
    }

    public StepOutcome Read(string app, string entitySet, IReadOnlyDictionary<string, string> query)
    {
        var type = executor.Registry.EntitySetIn(app, entitySet)
            ?? throw new InvalidOperationException($"App '{app}' publishes no entity set named '{entitySet}'.");
        using var session = executor.OpenRead(caller);
        return Rows(session.Set(type), query);
    }

    public StepOutcome ReadFunction(string app, string name, JsonObject parameters, IReadOnlyDictionary<string, string> query)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var model = executor.Registry.ReadingFunctionByPublicName(app, name)
            ?? throw new InvalidOperationException($"App '{app}' publishes no reading function named '{name}'.");
        using var session = executor.OpenRead(caller);
        using var document = JsonDocument.Parse(parameters.ToJsonString());
        return Rows(session.Invoke(model, document.RootElement.Clone()), query);
    }

    public StepOutcome Sql(string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        var rows = new JsonArray();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : JsonValue.Create(reader.GetValue(i).ToString());
            }

            rows.Add(row);
        }

        return new StepOutcome(true, new JsonObject { ["value"] = rows });
    }

    /// <summary>
    /// Giá trị từ CSV luôn là chuỗi; đổi theo kiểu tham số của command như Tester Tool (bool, số). Chuỗi rỗng cho tham số
    /// không phải chuỗi thì bỏ.
    /// </summary>
    private static JsonObject Typed(JsonObject parameters, Type commandType)
    {
        var typed = new JsonObject();
        foreach (var (name, value) in parameters)
        {
            var property = commandType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var clr = property is null ? null : Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (clr is null || value is not JsonValue v || !v.TryGetValue<string>(out var text))
            {
                typed[name] = value?.DeepClone();
                continue;
            }

            if (clr != typeof(string) && text.Length == 0)
            {
                continue;
            }

            typed[name] = clr switch
            {
                _ when clr == typeof(bool) => JsonValue.Create(bool.Parse(text)),
                _ when clr == typeof(int) => JsonValue.Create(int.Parse(text, CultureInfo.InvariantCulture)),
                _ when clr == typeof(decimal) => JsonValue.Create(decimal.Parse(text, CultureInfo.InvariantCulture)),
                _ => JsonValue.Create(text),
            };
        }

        return typed;
    }

    private static StepOutcome Rows(IQueryable source, IReadOnlyDictionary<string, string> query)
    {
        try
        {
            var (rows, _) = ODataQuery.Apply(source, query);
            var value = new JsonArray();
            foreach (var row in rows)
            {
                value.Add(JsonSerializer.SerializeToNode(row, row.GetType(), RowJson));
            }

            return new StepOutcome(true, new JsonObject { ["value"] = value });
        }
        catch (ODataQueryException e)
        {
            return new StepOutcome(false, new JsonObject { ["error"] = e.Message }, e.Message);
        }
    }
}
