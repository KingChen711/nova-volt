using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nvm.Solution.Suites;

/// <summary>
/// Một suite kiểu Tester Tool (file <c>*.suite.json</c>): các test definition, mỗi cái một chuỗi step. Test definition có
/// <see cref="TestDefinition.Csv"/> thì chạy một lượt cho mỗi dòng CSV; dòng đầu của CSV là tên cột.
/// </summary>
public sealed class SuiteFile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Step đầu tiên trượt thì dừng cả suite.</summary>
    public bool BreakOnError { get; set; } = true;

    public List<TestDefinition> TestDefinitions { get; set; } = [];

    public static SuiteFile Load(string path)
    {
        var suite = JsonSerializer.Deserialize<SuiteFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is not a suite.");
        if (string.IsNullOrEmpty(suite.Name))
        {
            suite.Name = Path.GetFileName(path).Replace(".suite.json", string.Empty, StringComparison.Ordinal);
        }

        return suite;
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class TestDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>File CSV cạnh suite; mỗi dòng một lượt, đọc giá trị bằng <c>#TEST_CSV.&lt;cột&gt;#</c>.</summary>
    public string? Csv { get; set; }

    public List<TestStep> TestSteps { get; set; } = [];
}

public enum StepType
{
    /// <summary>POST command theo tên công bố trên app.</summary>
    Command,

    /// <summary>Đọc entity set của app, có tham số OData (<c>$filter</c>, <c>$top</c>…).</summary>
    Read,

    /// <summary>Gọi reading function của app.</summary>
    Function,

    /// <summary>Câu SQL chỉ đọc trên DB MOM.</summary>
    Sql,
}

/// <summary>
/// Một step. Chuỗi trong <see cref="Parameters"/>, <see cref="Query"/>, <see cref="Sql"/> nhận hai loại tham chiếu:
/// <c>#TEST_CSV.&lt;cột&gt;#</c> (dòng CSV đang chạy) và <c>#GET-&gt;&lt;ID step&gt;.Result.&lt;đường dẫn&gt;#</c> (kết quả của step trước).
/// </summary>
public sealed class TestStep
{
    public string ID { get; set; } = string.Empty;

    public StepType StepType { get; set; }

    public string? App { get; set; }

    /// <summary>Tên command công bố (Command), entity set (Read) hoặc reading function (Function).</summary>
    public string? Name { get; set; }

    public JsonObject? Parameters { get; set; }

    public Dictionary<string, string>? Query { get; set; }

    public string? Sql { get; set; }

    /// <summary>Tắt: step phải lỗi thì mới đạt. Đạt với bất kỳ lỗi nào.</summary>
    public bool ExpectedSuccess { get; set; } = true;

    /// <summary>Bật: step này "có kết quả" (đọc ra ít nhất một dòng, hoặc command thành công) thì bỏ qua step kế tiếp.</summary>
    public bool IfThisSkipNext { get; set; }

    public List<StepAssert> Asserts { get; set; } = [];
}

/// <summary>Khẳng định trên kết quả của step theo đường dẫn JSON (vd <c>value[0].Status</c>).</summary>
public sealed class StepAssert
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Giá trị mong đợi; trong file suite là thuộc tính <c>Equals</c>.</summary>
    [JsonPropertyName("Equals")]
    public JsonNode? Expected { get; set; }

    /// <summary>Số phần tử của mảng tại <see cref="Path"/>.</summary>
    public int? Count { get; set; }
}

/// <summary>Kết quả của một step trên đích chạy: thành công hay không, và JSON để step sau tham chiếu.</summary>
public sealed record StepOutcome(bool Success, JsonNode Result, string? Message = null);

/// <summary>Nơi suite chạy: trong tiến trình (deploy, test) hoặc qua HTTP.</summary>
public interface ISuiteTarget
{
    StepOutcome Command(string app, string command, JsonObject parameters);

    StepOutcome Read(string app, string entitySet, IReadOnlyDictionary<string, string> query);

    StepOutcome ReadFunction(string app, string name, JsonObject parameters, IReadOnlyDictionary<string, string> query);

    StepOutcome Sql(string sql);
}
