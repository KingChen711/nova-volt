using System.Text.Json;

namespace Nvm.Simulator.Reporting;

/// <summary>Puts a <see cref="RunReport"/> on disk, and reads one back.</summary>
public static class RunReportFile
{
    private const string TemporarySuffix = ".tmp";

    /// <summary>Writes the report, replacing whatever was there.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="report">What to write.</param>
    /// <remarks>
    /// Written beside the target and then moved onto it. The reconciliation reads this file <b>while
    /// the run is still going</b>, and half a JSON document does not parse as half a report — it
    /// parses as nothing, which reads as a simulator that produced no measurements. A rename is
    /// atomic on both filesystems this runs on, so a reader sees the old report or the new one and
    /// never a partial one.
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

    /// <summary>Reads a report back.</summary>
    /// <param name="path">The file to read.</param>
    /// <exception cref="InvalidOperationException">The file is not a report.</exception>
    public static RunReport Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return JsonSerializer.Deserialize(File.ReadAllText(path), RunReportJsonContext.Default.RunReport)
            ?? throw new InvalidOperationException($"'{path}' holds no run report.");
    }
}
