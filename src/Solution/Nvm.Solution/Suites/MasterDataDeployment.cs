using System.Text.Json;

namespace Nvm.Solution.Suites;

/// <summary>Khi nào tên suite được ghi vào file lịch sử.</summary>
public enum HistoryPolicy
{
    /// <summary>Chỉ khi suite đạt.</summary>
    OnSuccess = 1,

    /// <summary>
    /// Cả khi suite trượt: biến thể của hệ thật đã quan sát (log deploy toàn lỗi mà tên suite vẫn vào lịch sử), vì sao thì
    /// chưa xác minh. Lab L07.
    /// </summary>
    Always = 2,
}

public enum SuiteStatus
{
    Executed,
    Failed,
    Skipped,
}

public sealed record DeployedSuite(string App, string Suite, SuiteStatus Status, IReadOnlyList<string> Log);

/// <summary>
/// Bước nạp master data lúc deploy: duyệt app theo <c>masterDataOrder</c> của <c>config.json</c>, trong mỗi app theo tên file;
/// suite có tên trong file lịch sử thì bỏ qua, không đếm dữ liệu. Thư mục: <c>&lt;root&gt;/master-data/&lt;App&gt;/*.suite.json</c>.
/// </summary>
public static class MasterDataDeployment
{
    public const string HistoryFileName = "MasterDataExecutionHistory.txt";

    public static IReadOnlyList<DeployedSuite> Run(string root, ISuiteTarget target, string historyFile, HistoryPolicy policy = HistoryPolicy.OnSuccess)
    {
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json")));
        var order = config.RootElement.GetProperty("masterDataOrder").EnumerateArray().Select(e => e.GetString()!).ToList();
        var history = File.Exists(historyFile)
            ? new HashSet<string>(File.ReadAllLines(historyFile).Where(l => l.Length > 0), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var report = new List<DeployedSuite>();

        foreach (var app in order)
        {
            var folder = Path.Combine(root, "master-data", app);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(folder, "*.suite.json").Order(StringComparer.Ordinal))
            {
                var name = SuiteFile.Load(file).Name;
                if (history.Contains(name))
                {
                    report.Add(new DeployedSuite(app, name, SuiteStatus.Skipped, []));
                    continue;
                }

                var result = SuiteRunner.Run(file, target);
                report.Add(new DeployedSuite(app, name, result.Passed ? SuiteStatus.Executed : SuiteStatus.Failed, result.Log));
                if (result.Passed || policy == HistoryPolicy.Always)
                {
                    File.AppendAllLines(historyFile, [name]);
                    history.Add(name);
                }
            }
        }

        return report;
    }
}
