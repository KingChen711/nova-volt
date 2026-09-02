using System.Text.Json;

namespace Nvm.Simulator.Reporting;

/// <summary>Đặt một <see cref="RunReport"/> xuống đĩa, và đọc lại nó.</summary>
public static class RunReportFile
{
    private const string TemporarySuffix = ".tmp";

    /// <summary>Ghi report, thay thế bất cứ thứ gì đang có ở đó.</summary>
    /// <param name="path">File đi tới đâu.</param>
    /// <param name="report">Cái gì cần ghi.</param>
    /// <remarks>
    /// Được ghi cạnh đích rồi mới di chuyển đè lên đích. Reconciliation đọc file này <b>trong khi run
    /// vẫn đang chạy</b>, và một nửa JSON document không parse thành một nửa report — nó parse thành
    /// không có gì cả, và điều đó đọc như thể simulator không tạo ra measurement nào. Việc đổi tên
    /// (rename) là atomic trên cả hai filesystem mà thứ này chạy trên, nên một reader chỉ thấy report
    /// cũ hoặc report mới chứ không bao giờ thấy một report dở dang.
    /// </remarks>
    public static void Write(string path, RunReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + TemporarySuffix;

        File.WriteAllText(temporary, JsonSerializer.Serialize(report, RunReportJsonContext.Default.RunReport));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Đọc lại một report.</summary>
    /// <param name="path">File cần đọc.</param>
    /// <exception cref="InvalidOperationException">File này không phải một report.</exception>
    public static RunReport Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return JsonSerializer.Deserialize(File.ReadAllText(path), RunReportJsonContext.Default.RunReport)
            ?? throw new InvalidOperationException($"'{path}' holds no run report.");
    }
}
