using System.Globalization;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Đọc file CSV mà một máy test cuối chuyền xuất lên một share.</summary>
/// <remarks>
/// <para>
/// Tự viết tay thay vì dùng thư viện CSV, và lý do nằm ở hình dạng của input: sáu cột cố định là
/// định danh nhà máy, không có quoting, không có newline nhúng bên trong, do một máy từ năm 2011
/// tạo ra và sẽ không bao giờ đổi định dạng. Một dependency ở đây sẽ mua về sự linh hoạt mà file này
/// không cần, và đặt một parser chắn giữa người vận hành với dây chuyền họ cần sửa.
/// </para>
/// <para>
/// <b>Một dòng hỏng không làm cả file bị từ chối.</b> Một trăm cell đã được test; chín mươi chín kết
/// quả trong số đó là dữ liệu tốt, và vứt bỏ chúng chỉ vì dòng thứ một trăm có một con số dị dạng
/// nghĩa là người vận hành hoặc mất chín mươi chín lần đo, hoặc phải tự tay sửa file export. Mỗi dòng
/// đứng hay đổ một mình, và những dòng đổ sẽ nói rõ vì sao.
/// </para>
/// </remarks>
public sealed class CsvMeasurementReader
{
    /// <summary>Header mà mọi file được chấp nhận phải bắt đầu bằng, nguyên văn.</summary>
    public const string Header = "equipment_path,unit_id,signal_code,measured_at,value_kind,value";

    private const char Separator = ',';
    private const int ColumnCount = 6;

    private readonly IEquipmentDirectory _equipment;

    /// <summary>Tạo một reader để phân giải path dựa trên model đang chạy của nhà máy.</summary>
    /// <param name="equipment">Model mà nhà máy đang chạy tại thời điểm hiện tại.</param>
    public CsvMeasurementReader(IEquipmentDirectory equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);

        _equipment = equipment;
    }

    /// <summary>Đọc toàn bộ một file, từng dòng một.</summary>
    /// <param name="lines">Mọi dòng của file, header đứng đầu.</param>
    /// <returns>Những measurement đã đọc được và những dòng không đọc được.</returns>
    /// <exception cref="FileDropFormatException">
    /// File không có header hoặc header sai. Đây là lỗi của cả file chứ không phải của một dòng, và
    /// không có cách nào an toàn để đoán cột nào là cột nào.
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

            // Đọc identity trước, và nhớ giá trị ở ngoài khối try. Một dòng lỗi ở cột VALUE thì đã
            // cho biết trước nó là máy nào, và thông tin đó phải sống sót qua thất bại: phép kiểm
            // single-machine phía sau chỉ đúng khi nó thấy mọi máy mà file có nêu tên, chứ không chỉ
            // những máy mà dòng của chúng đọc trót lọt.
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

    /// <summary>Chỉ đọc dòng này nói về ai, trước khi đọc nó đo được cái gì.</summary>
    private EquipmentPath ParseIdentity(string[] columns)
    {
        var equipmentPath = EquipmentPath.Parse(columns[0].Trim());

        // Cùng một phép kiểm phía server mà đường MQTT thực hiện (K3). Một file nêu tên một nhà máy
        // chưa ai kích hoạt, hoặc một máy đã ngừng vận hành, sẽ bị từ chối ở đây thay vì được lưu dưới
        // một path trỏ tới hư không — một dòng dữ liệu mà không truy vấn nào tìm ra và không báo cáo
        // nào phát hiện thiếu.
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

        // Round-trip, bắt buộc có offset. Một máy test xuất giờ tường (local wall-clock) không kèm
        // offset là mập mờ trong đúng một giờ mỗi mùa thu tại DE1, và measured_at là một phần của
        // natural key — một khoá mập mờ sẽ gộp hai measurement khác nhau thành một qua dedup.
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

    // TryParse chấp nhận một chuỗi trần "2026-08-28T09:28:11" và âm thầm giả định múi giờ local của
    // máy. Phép kiểm nằm trên chuỗi văn bản vì đến khi nó đã thành DateTimeOffset thì giả định đó
    // không còn nhìn thấy được nữa.
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

            // Viết tường minh thay vì bool.TryParse: máy test ghi "true"/"false" chữ thường, và nếu
            // chấp nhận cả "True" thì hai cách viết của cùng một file có thể cùng tồn tại cho tới khi
            // một trong hai gặp phải một reader khắt khe hơn.
            "boolean" when raw is "true" or "false" => new MetricValue.Flag(raw == "true"),

            "text" => new MetricValue.Text(raw),

            "real" or "integer" or "boolean" => throw new FormatException(
                $"'{raw}' is not a valid {kind} value."),

            _ => throw new FormatException(
                $"'{kind}' is not a value kind. Expected real, integer, boolean or text."),
        };
}

/// <summary>Cả file không đọc được, nên không dòng nào trong đó đáng tin.</summary>
/// <param name="message">File có vấn đề gì.</param>
public sealed class FileDropFormatException(string message) : Exception(message);
