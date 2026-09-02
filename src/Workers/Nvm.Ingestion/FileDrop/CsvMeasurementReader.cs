using System.Globalization;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Reads the CSV an end-of-line tester exports onto a share.</summary>
/// <remarks>
/// <para>
/// Hand-written rather than a CSV library, and the reason is the shape of the input: six fixed
/// columns of plant identifiers, no quoting, no embedded newlines, produced by a machine from 2011
/// that will never change its format. A dependency here would buy flexibility this file has no use
/// for and would put a parser between an operator and the line they need to fix.
/// </para>
/// <para>
/// <b>One bad line does not reject the file.</b> A hundred cells were tested; ninety-nine of those
/// results are good data, and throwing them away because a hundredth row has a malformed number
/// means an operator either loses ninety-nine measurements or hand-edits an export. Each line
/// stands or falls alone, and the ones that fall say why.
/// </para>
/// </remarks>
public sealed class CsvMeasurementReader
{
    /// <summary>The header every accepted file starts with, verbatim.</summary>
    public const string Header = "equipment_path,unit_id,signal_code,measured_at,value_kind,value";

    private const char Separator = ',';
    private const int ColumnCount = 6;

    private readonly IEquipmentDirectory _equipment;

    /// <summary>Creates a reader that resolves paths against the plant's active model.</summary>
    /// <param name="equipment">The model each plant is currently running.</param>
    public CsvMeasurementReader(IEquipmentDirectory equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        _equipment = equipment;
    }

    /// <summary>Parses a whole file, line by line.</summary>
    /// <param name="lines">Every line of the file, header first.</param>
    /// <returns>The measurements that parsed and the lines that did not.</returns>
    /// <exception cref="FileDropFormatException">
    /// The file has no header or the wrong one. That is a fault of the whole file rather than of a
    /// line, and there is no safe way to guess which column is which.
    /// </exception>
    public FileDropParseResult Read(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
        {
            throw new FileDropFormatException(
                $"The file must start with the header '{Header}'. Without it, the columns can only be "
                + "guessed, and a guess that puts the value in the signal column produces rows that "
                + "look valid and mean nothing.");
        }

        var measurements = new List<FileMeasurement>();
        var rejected = new List<RejectedLine>();

        for (var index = 1; index < lines.Count; index++)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Identity first, and remembered outside the try. A line that fails on its VALUE has
            // already told us whose machine it is, and that fact has to survive the failure: the
            // single-machine check downstream is only sound if it sees every machine the file names,
            // not only the machines whose lines happened to parse all the way through.
            EquipmentPath? identity = null;

            try
            {
                var columns = SplitColumns(line);
                identity = ParseIdentity(columns);
                measurements.Add(ParseMeasurement(columns, identity));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                rejected.Add(new RejectedLine(index + 1, line, exception.Message, identity));
            }
        }

        return new FileDropParseResult(measurements, rejected);
    }

    private static string[] SplitColumns(string line)
    {
        var columns = line.Split(Separator);

        if (columns.Length != ColumnCount)
        {
            throw new FormatException(
                $"Expected {ColumnCount} columns and found {columns.Length}. Header: '{Header}'.");
        }

        return columns;
    }

    /// <summary>Reads only who the line is about, before anything about what it measured.</summary>
    private EquipmentPath ParseIdentity(string[] columns)
    {
        var equipmentPath = EquipmentPath.Parse(columns[0].Trim());

        // The same server-side check the MQTT path makes (K3). A file naming a plant nobody activated,
        // or a machine that was decommissioned, is refused here rather than stored under a path that
        // resolves to nothing — a row no query will ever find and no report will ever miss.
        if (_equipment.FindDevice(LineOf(equipmentPath), equipmentPath.Code) is null
            && !_equipment.Contains(equipmentPath))
        {
            throw new ArgumentException(
                $"'{equipmentPath.Value}' is not in the active model of plant '{equipmentPath.SiteId}'.");
        }

        return equipmentPath;
    }

    private static FileMeasurement ParseMeasurement(string[] columns, EquipmentPath equipmentPath)
    {
        var unitId = string.IsNullOrWhiteSpace(columns[1]) ? null : columns[1].Trim();
        var signalCode = columns[2].Trim();

        if (string.IsNullOrEmpty(signalCode))
        {
            throw new FormatException("The signal code is empty, so the reading names nothing measured.");
        }

        // Round-trip, offset required. A tester exporting a local wall-clock time with no offset is
        // ambiguous for one hour every autumn at DE1, and measured_at is part of the natural key —
        // an ambiguous key deduplicates two different measurements into one.
        if (!DateTimeOffset.TryParse(
                columns[3].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var measuredAt)
            || !HasExplicitOffset(columns[3]))
        {
            throw new FormatException(
                $"'{columns[3].Trim()}' is not a timestamp with an explicit UTC offset (K2).");
        }

        var value = ParseValue(columns[4].Trim(), columns[5].Trim());

        return new FileMeasurement(
            equipmentPath,
            unitId,
            new DeviceReading(signalCode, Alias: null, value, measuredAt));
    }

    // TryParse accepts a bare "2026-08-28T09:28:11" and quietly assumes the machine's local zone.
    // The check is on the text because by the time it is a DateTimeOffset that assumption is invisible.
    private static bool HasExplicitOffset(string text)
    {
        var trimmed = text.Trim();

        return trimmed.EndsWith('Z')
            || (trimmed.Length > 6 && (trimmed[^6] is '+' or '-') && trimmed[^3] == ':');
    }

    private static EquipmentPath LineOf(EquipmentPath path)
    {
        var segments = path.Segments;

        return segments.Length <= 4
            ? path
            : EquipmentPath.Parse(string.Join(EquipmentPath.Separator, segments.Take(4)));
    }

    private static MetricValue ParseValue(string kind, string raw) =>
        kind switch
        {
            "real" when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) =>
                new MetricValue.Real(real),

            "integer" when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                new MetricValue.Integral(integer),

            // Written out rather than bool.TryParse: the tester writes "true"/"false" lower case, and
            // accepting "True" as well would let two spellings of the same file coexist until one of
            // them met a stricter reader.
            "boolean" when raw is "true" or "false" => new MetricValue.Flag(raw == "true"),

            "text" => new MetricValue.Text(raw),

            "real" or "integer" or "boolean" => throw new FormatException(
                $"'{raw}' is not a valid {kind} value."),

            _ => throw new FormatException(
                $"'{kind}' is not a value kind. Expected real, integer, boolean or text."),
        };
}

/// <summary>The file as a whole cannot be read, so no line in it can be trusted.</summary>
/// <param name="message">What is wrong with the file.</param>
public sealed class FileDropFormatException(string message) : Exception(message);
