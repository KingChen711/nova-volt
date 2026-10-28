using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nvm.Solution.Suites;

/// <summary>Kết quả chạy một suite: đạt hay không, và log từng step như Execution Log của Tester Tool.</summary>
public sealed record SuiteResult(string Suite, bool Passed, IReadOnlyList<string> Log);

/// <summary>
/// Chạy suite trên một đích. Hành vi bám theo Tester Tool đã quan sát: tham chiếu tới cột CSV không có thì tham số đó
/// bị bỏ (bước đọc trả mọi dòng, step sau lấy dòng đầu: lab L16); <c>ExpectedSuccess = false</c> đạt với mọi lỗi.
/// </summary>
public static partial class SuiteRunner
{
    public static SuiteResult Run(string suitePath, ISuiteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var suite = SuiteFile.Load(suitePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(suitePath))!;
        var log = new List<string>();
        var passed = true;

        foreach (var definition in suite.TestDefinitions)
        {
            var rows = definition.Csv is null ? [new Dictionary<string, string>(StringComparer.Ordinal)] : ReadCsv(Path.Combine(directory, definition.Csv));
            for (var r = 0; r < rows.Count; r++)
            {
                var results = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
                var skipNext = false;
                foreach (var step in definition.TestSteps)
                {
                    var label = rows.Count > 1 ? $"{definition.Name}#{r + 1}/{step.ID}" : $"{definition.Name}/{step.ID}";
                    if (skipNext)
                    {
                        log.Add($"Step [{label}] Skipped");
                        skipNext = false;
                        continue;
                    }

                    var outcome = Execute(step, target, rows[r], results);
                    results[step.ID] = outcome.Result;
                    var failure = outcome.Success != step.ExpectedSuccess
                        ? outcome.Message ?? (outcome.Success ? "expected a failure" : "failed")
                        : FailedAssert(step, outcome.Result);
                    if (failure is not null)
                    {
                        log.Add($"Step [{label}] Failed - {failure}");
                        passed = false;
                        if (suite.BreakOnError)
                        {
                            return new SuiteResult(suite.Name, false, log);
                        }

                        continue;
                    }

                    log.Add($"Step [{label}] Passed");
                    skipNext = step.IfThisSkipNext && Found(step, outcome);
                }
            }
        }

        return new SuiteResult(suite.Name, passed, log);
    }

    private static StepOutcome Execute(TestStep step, ISuiteTarget target, IReadOnlyDictionary<string, string> row, Dictionary<string, JsonNode> results)
    {
        try
        {
            var query = ResolveQuery(step.Query, row, results);
            return step.StepType switch
            {
                StepType.Command => target.Command(step.App!, step.Name!, ResolveObject(step.Parameters, row, results)),
                StepType.Read => target.Read(step.App!, step.Name!, query),
                StepType.Function => target.ReadFunction(step.App!, step.Name!, ResolveObject(step.Parameters, row, results), query),
                StepType.Sql => target.Sql(ResolveText(step.Sql ?? string.Empty, row, results) ?? string.Empty),
                _ => new StepOutcome(false, new JsonObject(), $"Unknown step type {step.StepType}."),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            return new StepOutcome(false, new JsonObject { ["error"] = e.Message }, e.Message);
        }
    }

    /// <summary>"Có kết quả": đọc ra ít nhất một dòng, hoặc command thành công.</summary>
    private static bool Found(TestStep step, StepOutcome outcome) => step.StepType == StepType.Command
        ? outcome.Success
        : outcome.Result["value"] is JsonArray { Count: > 0 };

    private static string? FailedAssert(TestStep step, JsonNode result)
    {
        foreach (var assert in step.Asserts)
        {
            var actual = Navigate(result, assert.Path);
            if (assert.Count is { } count && (actual as JsonArray)?.Count != count)
            {
                return $"assert {assert.Path}: expected {count} items, got {(actual as JsonArray)?.Count.ToString(CultureInfo.InvariantCulture) ?? "none"}";
            }

            if (assert.Expected is not null && !JsonNode.DeepEquals(actual, assert.Expected))
            {
                return $"assert {assert.Path}: expected {assert.Expected.ToJsonString()}, got {actual?.ToJsonString() ?? "null"}";
            }
        }

        return null;
    }

    private static Dictionary<string, string> ResolveQuery(Dictionary<string, string>? query, IReadOnlyDictionary<string, string> row,
        Dictionary<string, JsonNode> results)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in query ?? [])
        {
            // Tham chiếu không giải được (vd cột CSV không có): bỏ cả tham số, như Tester Tool.
            if (ResolveText(value, row, results) is { } text)
            {
                resolved[key] = text;
            }
        }

        return resolved;
    }

    private static JsonObject ResolveObject(JsonObject? parameters, IReadOnlyDictionary<string, string> row, Dictionary<string, JsonNode> results)
    {
        var resolved = new JsonObject();
        foreach (var (key, value) in parameters ?? [])
        {
            if (Resolve(value, row, results, out var node))
            {
                resolved[key] = node;
            }
        }

        return resolved;
    }

    private static bool Resolve(JsonNode? value, IReadOnlyDictionary<string, string> row, Dictionary<string, JsonNode> results, out JsonNode? node)
    {
        node = value?.DeepClone();
        switch (value)
        {
            case JsonObject obj:
                node = ResolveObject(obj, row, results);
                return true;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    if (Resolve(item, row, results, out var resolvedItem))
                    {
                        items.Add(resolvedItem);
                    }
                }

                node = items;
                return true;
            case JsonValue text when text.TryGetValue<string>(out var s):
                var whole = Token().Match(s);
                if (whole.Success && whole.Length == s.Length)
                {
                    // Cả chuỗi là một tham chiếu: giữ kiểu của giá trị (số, bool) thay vì ép thành chuỗi.
                    node = Lookup(whole, row, results)?.DeepClone();
                    return node is not null;
                }

                var replaced = ResolveText(s, row, results);
                node = replaced is null ? null : JsonValue.Create(replaced);
                return replaced is not null;
            default:
                return true;
        }
    }

    /// <summary>Thay mọi tham chiếu trong chuỗi; một tham chiếu không giải được thì trả null.</summary>
    private static string? ResolveText(string text, IReadOnlyDictionary<string, string> row, Dictionary<string, JsonNode> results)
    {
        var missing = false;
        var resolved = Token().Replace(text, match =>
        {
            var value = Lookup(match, row, results);
            if (value is null)
            {
                missing = true;
                return string.Empty;
            }

            return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"');
        });
        return missing ? null : resolved;
    }

    private static JsonNode? Lookup(Match match, IReadOnlyDictionary<string, string> row, Dictionary<string, JsonNode> results)
    {
        if (match.Groups["csv"].Success)
        {
            return row.TryGetValue(match.Groups["csv"].Value, out var cell) ? JsonValue.Create(cell) : null;
        }

        return results.TryGetValue(match.Groups["step"].Value, out var result) ? Navigate(result, match.Groups["path"].Value) : null;
    }

    /// <summary>Đi theo đường dẫn kiểu <c>value[0].Id</c>.</summary>
    internal static JsonNode? Navigate(JsonNode? node, string path)
    {
        foreach (Match part in PathPart().Matches(path))
        {
            node = part.Groups["index"].Success
                ? node is JsonArray array && int.Parse(part.Groups["index"].Value, CultureInfo.InvariantCulture) is var i && i < array.Count ? array[i] : null
                : (node as JsonObject)?[part.Groups["name"].Value];
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>CSV đơn giản (dấu phẩy, ngoặc kép); dòng đầu là tên cột.</summary>
    internal static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).Select(SplitCsv).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var header = lines[0];
        return [.. lines.Skip(1).Select(cells => header.Select((name, i) => (name, value: i < cells.Count ? cells[i] : string.Empty))
            .ToDictionary(c => c.name, c => c.value, StringComparer.Ordinal))];
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }

    [GeneratedRegex(@"#(?:TEST_CSV\.(?<csv>[^#]+)|GET->(?<step>[A-Za-z0-9_]+)\.Result\.?(?<path>[^#]*))#")]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_@$]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathPart();
}
