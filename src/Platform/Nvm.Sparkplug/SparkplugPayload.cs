using System.Collections.Immutable;
using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;
using ValueCase = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric.ValueOneofCase;

namespace Nvm.Sparkplug;

/// <summary>Biến các byte Sparkplug B thành device reading.</summary>
/// <remarks>
/// <para>
/// Cánh cửa duy nhất ra khỏi assembly này. Các type <c>Org.Eclipse.Tahu.Protobuf</c> được sinh ra từ
/// một spec ta không sở hữu (ADR-026), và để chúng lọt vào một signature ở bất kỳ nơi nào khác sẽ khiến
/// hình dạng của cả hệ thống phụ thuộc vào một file ta không được phép sửa.
/// </para>
/// <para>
/// Birth và data là hai method riêng vì một payload không tự nói mình là loại nào — message type nằm
/// trong MQTT topic (<c>spBv1.0/{group}/DBIRTH/{node}/{device}</c>, docs/scope.md §7.1), và C03 là nơi
/// đọc nó. Đoán từ hình dạng của payload sẽ đúng phần lớn thời gian, và đó lại chính là tần suất tệ
/// nhất để một phép đoán đúng.
/// </para>
/// <para>
/// Không có gì ở đây giữ state. Bảng alias được truyền vào rồi trả lại, nên vòng đời quan trọng — một
/// session của một edge node — vẫn do caller quản lý, và C11 là nơi nó trở thành node state được khóa
/// theo <c>bdSeq</c>.
/// </para>
/// </remarks>
public static class SparkplugPayload
{
    /// <summary>Tên của metric mang số session trong một birth hoặc một death.</summary>
    public const string BirthDeathSequenceMetric = "bdSeq";

    private const string NodeControlPrefix = "Node Control/";
    private const string DeviceControlPrefix = "Device Control/";

    /// <summary>Một metric có thuộc về protocol thay vì thuộc về nhà máy hay không.</summary>
    /// <param name="metricName">Tên metric như đã khai báo.</param>
    /// <remarks>
    /// <para>
    /// <c>bdSeq</c> và các metric <c>Control/</c> là Sparkplug đang nói về session của chính nó: đây là
    /// kết nối nào, và có ai đang yêu cầu rebirth hay không. Chúng đi như những metric bình thường vì
    /// spec không có chỗ nào khác để đặt chúng, và đó chính xác là cái bẫy.
    /// </para>
    /// <para>
    /// Nếu lưu thành telemetry, chúng sẽ là các phép đo mà không thiết bị nào thực hiện, trên một
    /// channel không cell nào ngồi trong đó — và D1 đếm các phép đo logic theo số dòng, nên mỗi lần
    /// node birth sẽ làm lệch việc đối soát đi đúng bằng số metric control mà nó mang theo. Một tổng
    /// bị lệch một lượng cố định là loại khó nhận ra nhất, vì nó trông giống một tranh cãi về làm tròn
    /// hơn là dữ liệu lẽ ra không nên tồn tại.
    /// </para>
    /// </remarks>
    public static bool IsProtocolMetric(string? metricName) =>
        metricName is not null
        && (string.Equals(metricName, BirthDeathSequenceMetric, StringComparison.Ordinal)
            || metricName.StartsWith(NodeControlPrefix, StringComparison.Ordinal)
            || metricName.StartsWith(DeviceControlPrefix, StringComparison.Ordinal));

    /// <summary>Decode một birth payload: mọi metric tự đặt tên và tự khai báo mình.</summary>
    /// <param name="payload">Các byte Sparkplug B thô.</param>
    /// <returns>Các reading mà birth mang theo, và bảng alias mà nó thiết lập.</returns>
    /// <exception cref="SparkplugDecodeException">
    /// Các byte không phải một payload Sparkplug, hoặc một metric thiếu tên, datatype, giá trị hay
    /// timestamp mà một birth bắt buộc phải mang theo.
    /// </exception>
    public static SparkplugBirth DecodeBirth(ReadOnlySpan<byte> payload)
    {
        var message = Parse(payload);
        var payloadTimestamp = message.HasTimestamp ? message.Timestamp : (ulong?)null;

        var readings = ImmutableArray.CreateBuilder<DeviceReading>(message.Metrics.Count);
        var definitions = new Dictionary<ulong, MetricDefinition>(message.Metrics.Count);
        var names = new HashSet<string>(message.Metrics.Count, StringComparer.Ordinal);

        foreach (var metric in message.Metrics)
        {
            if (!metric.HasName || string.IsNullOrWhiteSpace(metric.Name))
            {
                throw new SparkplugDecodeException(
                    "A birth metric carries no name. A birth is the declaration every later message "
                    + "is read against; one that leaves a metric unnamed makes that metric "
                    + "permanently unreadable.");
            }

            // Bắt buộc chặt chẽ thay vì suy ra từ giá trị trên wire, và lý do là signedness: Int32 và
            // UInt32 đều đi qua int_value, nên một birth không khai báo sẽ để mọi reading của metric
            // đó mập mờ trong suốt phần còn lại của session.
            if (!metric.HasDatatype)
            {
                throw new SparkplugDecodeException(
                    $"Birth metric '{metric.Name}' does not declare a datatype.");
            }

            if (!names.Add(metric.Name))
            {
                throw new SparkplugDecodeException(
                    $"Birth declares metric '{metric.Name}' twice. Two readings under one name cannot "
                    + "both be current, and nothing downstream could tell which one it has.");
            }

            var dataType = (DataType)metric.Datatype;
            var alias = metric.HasAlias ? metric.Alias : (ulong?)null;

            if (alias is { } number && !definitions.TryAdd(number, new MetricDefinition(metric.Name, dataType)))
            {
                throw new SparkplugDecodeException(
                    $"Birth gives alias {number} to both '{definitions[number].Name}' and "
                    + $"'{metric.Name}'. Every later message using it would be attributed to whichever "
                    + "of the two was read last.");
            }

            readings.Add(new DeviceReading(
                metric.Name,
                alias,
                ReadValue(metric, dataType, metric.Name),
                ReadTimestamp(metric, payloadTimestamp, metric.Name)));
        }

        return new SparkplugBirth(
            readings.DrainToImmutable(),
            MetricAliasTable.From(definitions),
            message.HasSeq ? message.Seq : null,
            ReadBirthDeathSequence(message));
    }

    /// <summary>Decode một data payload dựa trên các alias mà birth của nó đã thiết lập.</summary>
    /// <param name="payload">Các byte Sparkplug B thô.</param>
    /// <param name="aliases">Bảng từ birth của session này của node này.</param>
    /// <returns>Các reading đã thay đổi. Report-by-exception nghĩa là bình thường đây là một danh sách ngắn.</returns>
    /// <exception cref="UnknownMetricAliasException">
    /// Một metric chỉ được định danh bằng một alias mà bảng không có. Hãy yêu cầu rebirth.
    /// </exception>
    /// <exception cref="SparkplugDecodeException">
    /// Các byte không phải một payload Sparkplug, hoặc một metric không mang identity, giá trị hay
    /// timestamp nào.
    /// </exception>
    public static ImmutableArray<DeviceReading> DecodeData(ReadOnlySpan<byte> payload, MetricAliasTable aliases) =>
        DecodeData(payload, aliases, out _);

    /// <summary>Decode một data payload và báo cáo <c>seq</c> đi kèm với nó.</summary>
    /// <param name="payload">Các byte Sparkplug B thô.</param>
    /// <param name="aliases">Bảng từ birth của session này của node này.</param>
    /// <param name="sequence">Trường <c>seq</c> của payload, hoặc null khi payload bỏ qua nó.</param>
    /// <returns>Các reading đã thay đổi.</returns>
    /// <remarks>
    /// Một overload thay vì một kiểu trả về phong phú hơn, để việc đọc sequence chỉ tốn một lần parse
    /// thay vì hai. Ở năm nghìn message mỗi giây, sự khác biệt này không chỉ là lý thuyết, và sequence
    /// đúng là trường mà caller phải thấy trên message nó đang decode rồi.
    /// </remarks>
    /// <exception cref="UnknownMetricAliasException">
    /// Một metric chỉ được định danh bằng một alias mà bảng không có. Hãy yêu cầu rebirth.
    /// </exception>
    /// <exception cref="SparkplugDecodeException">
    /// Các byte không phải một payload Sparkplug, hoặc một metric không mang identity, giá trị hay
    /// timestamp nào.
    /// </exception>
    public static ImmutableArray<DeviceReading> DecodeData(
        ReadOnlySpan<byte> payload,
        MetricAliasTable aliases,
        out ulong? sequence)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        var message = Parse(payload);
        sequence = message.HasSeq ? message.Seq : null;
        var payloadTimestamp = message.HasTimestamp ? message.Timestamp : (ulong?)null;

        var readings = ImmutableArray.CreateBuilder<DeviceReading>(message.Metrics.Count);

        foreach (var metric in message.Metrics)
        {
            var alias = metric.HasAlias ? metric.Alias : (ulong?)null;
            var declared = metric.HasDatatype ? (DataType)metric.Datatype : (DataType?)null;
            string name;

            if (metric.HasName && !string.IsNullOrWhiteSpace(metric.Name))
            {
                // Một metric có tên tự mô tả chính nó, nên nó được chấp nhận kể cả giữa session.
                // Datatype của nó vẫn đến từ birth khi wire bỏ qua trường này — birth vẫn là nơi duy
                // nhất từng nói ra signedness của một số nguyên.
                name = metric.Name;

                if (alias is { } named && aliases.TryResolve(named, out var byAlias))
                {
                    if (!string.Equals(byAlias.Name, name, StringComparison.Ordinal))
                    {
                        // Reading này có thể được ghi nhận đúng — vì nó tự đặt tên mình. Reading kế
                        // tiếp dùng cùng alias thì không thể, và sẽ bị gán cho metric mà bảng cũ vẫn
                        // còn nhớ. Việc đánh số lại được công bố bằng một birth, không phải lén lút
                        // trong một DDATA.
                        throw new UnknownMetricAliasException(
                            $"Alias {named} arrived naming '{name}', but the birth gave it to "
                            + $"'{byAlias.Name}'. The node has renumbered without announcing it; "
                            + "request a rebirth rather than keeping a table that is already wrong.");
                    }

                    declared ??= byAlias.DataType;
                }
            }
            else if (alias is { } number)
            {
                if (!aliases.TryResolve(number, out var definition))
                {
                    throw new UnknownMetricAliasException(number, aliases.Count);
                }

                name = definition.Name;
                declared ??= definition.DataType;
            }
            else
            {
                throw new SparkplugDecodeException(
                    "A metric carries neither a name nor an alias, so there is nothing to attribute "
                    + "its value to.");
            }

            readings.Add(new DeviceReading(
                name,
                alias,
                ReadValue(metric, declared, name),
                ReadTimestamp(metric, payloadTimestamp, name)));
        }

        return readings.DrainToImmutable();
    }

    /// <summary>Decode một <c>NDEATH</c> và báo cáo nó kết thúc session nào.</summary>
    /// <param name="payload">Các byte Sparkplug B thô của last will.</param>
    /// <returns>Định danh session mà death nêu tên.</returns>
    /// <exception cref="SparkplugDecodeException">Các byte không phải một payload Sparkplug.</exception>
    /// <remarks>
    /// Cố tình khoan dung ở nơi birth thì nghiêm ngặt. Một death được <b>broker</b> publish từ một
    /// will đã đăng ký lúc kết nối; node không có mặt ở đó để sửa nó, và từ chối một death vì một
    /// metric lỗi định dạng sẽ khiến một node bị đánh dấu sống mãi mãi — chính là kết quả duy nhất mà
    /// <c>NDEATH</c> tồn tại để ngăn chặn.
    /// </remarks>
    public static SparkplugDeath DecodeDeath(ReadOnlySpan<byte> payload) =>
        new(ReadBirthDeathSequence(Parse(payload)));

    /// <summary>Encode một birth: mọi metric khai báo tên, alias và type của nó.</summary>
    /// <param name="readings">Giá trị hiện tại của mọi metric mà device cung cấp.</param>
    /// <param name="sequence">Sparkplug <c>seq</c> của message này.</param>
    /// <param name="timestamp">Thời điểm device lắp ráp payload.</param>
    /// <exception cref="ArgumentException">
    /// Một reading không có giá trị để khai báo type từ đó, hoặc hai reading dùng chung tên hay alias.
    /// </exception>
    /// <remarks>
    /// Viết cho simulator ở C05, và đây là encoder duy nhất trong repo. Các fixture đã ghi lại trong
    /// <c>tests/Fixtures/sparkplug/</c> cố tình không đến từ nó — một decoder được kiểm tra dựa trên
    /// chính encoder của nó sẽ tự đồng thuận với chính mình ngay cả khi cả hai đều sai về schema.
    /// </remarks>
    public static byte[] EncodeBirth(IReadOnlyList<DeviceReading> readings, ulong sequence, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var payload = NewPayload(sequence, timestamp);

        foreach (var reading in readings)
        {
            // Một birth là một lời khai báo, và không có gì để khai báo về một metric mà type của nó
            // chỉ có thể biết được từ một giá trị nó không có. Sparkplug cho phép is_null lúc birth;
            // hệ thống này không tạo ra nó, và từ chối thì tốt hơn là bịa ra một type cho nó.
            if (reading.Value is MetricValue.Absent)
            {
                throw new ArgumentException(
                    $"Metric '{reading.MetricName}' is absent, so a birth cannot declare its type.",
                    nameof(readings));
            }

            var metric = new SparkplugMetric
            {
                Name = reading.MetricName,
                Datatype = (uint)DataTypeOf(reading.Value),
                Timestamp = ToUnixMilliseconds(reading.DeviceTimestamp),
            };

            if (reading.Alias is { } alias)
            {
                metric.Alias = alias;
            }

            Write(metric, reading.Value);
            payload.Metrics.Add(metric);
        }

        return payload.ToByteArray();
    }

    /// <summary>Encode một bản cập nhật report-by-exception: chỉ alias và giá trị, không gì khác.</summary>
    /// <param name="readings">Chỉ những metric có giá trị thay đổi.</param>
    /// <param name="sequence">Sparkplug <c>seq</c> của message này.</param>
    /// <param name="timestamp">Thời điểm device lắp ráp payload.</param>
    /// <remarks>
    /// Một reading có alias được ghi chỉ bằng alias mà thôi — không tên, không datatype — vì đó chính
    /// là toàn bộ sự tiết kiệm của protocol này, và vì ghi thêm những thứ đó dù sao cũng sẽ khiến
    /// encoder này tạo ra traffic mà không device thật nào tạo ra, ngược hẳn với mục đích của một
    /// simulator. Một reading không có alias thì rơi về dùng tên của nó.
    /// </remarks>
    public static byte[] EncodeData(IReadOnlyList<DeviceReading> readings, ulong sequence, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var payload = NewPayload(sequence, timestamp);

        foreach (var reading in readings)
        {
            var metric = new SparkplugMetric { Timestamp = ToUnixMilliseconds(reading.DeviceTimestamp) };

            if (reading.Alias is { } alias)
            {
                metric.Alias = alias;
            }
            else
            {
                metric.Name = reading.MetricName;
                metric.Datatype = (uint)DataTypeOf(reading.Value);
            }

            Write(metric, reading.Value);
            payload.Metrics.Add(metric);
        }

        return payload.ToByteArray();
    }

    // bdSeq đi như một metric bình thường thay vì một trường của payload, nên nó được đọc theo tên.
    // Cách viết được cố định bởi spec Sparkplug và cũng phân biệt hoa thường ở đó.
    private static ulong? ReadBirthDeathSequence(Payload message)
    {
        foreach (var metric in message.Metrics)
        {
            if (!metric.HasName || !string.Equals(metric.Name, BirthDeathSequenceMetric, StringComparison.Ordinal))
            {
                continue;
            }

            return metric.ValueCase switch
            {
                ValueCase.LongValue => metric.LongValue,
                ValueCase.IntValue => metric.IntValue,
                _ => null,
            };
        }

        return null;
    }

    private static Payload NewPayload(ulong sequence, DateTimeOffset timestamp) =>
        new() { Seq = sequence, Timestamp = ToUnixMilliseconds(timestamp) };

    private static ulong ToUnixMilliseconds(DateTimeOffset timestamp)
    {
        var milliseconds = timestamp.ToUnixTimeMilliseconds();

        return milliseconds < 0
            ? throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                timestamp,
                "Sparkplug timestamps are milliseconds since the Unix epoch and cannot be negative.")
            : (ulong)milliseconds;
    }

    private static DataType DataTypeOf(MetricValue value) =>
        value switch
        {
            // Double và Int64 thay vì type hẹp nhất vừa vặn. Một simulator phát ra Float cho reading
            // này rồi Double cho reading kế — chỉ vì một cái tình cờ tròn số — sẽ tạo ra một device có
            // type khai báo thay đổi giữa session, điều không thiết bị thật nào làm.
            MetricValue.Real => DataType.Double,
            MetricValue.Integral => DataType.Int64,
            MetricValue.Flag => DataType.Boolean,
            MetricValue.Text => DataType.String,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No Sparkplug datatype for this value."),
        };

    private static void Write(SparkplugMetric metric, MetricValue value)
    {
        switch (value)
        {
            case MetricValue.Real real:
                metric.DoubleValue = real.Value;
                break;
            case MetricValue.Integral integral:
                metric.LongValue = unchecked((ulong)integral.Value);
                break;
            case MetricValue.Flag flag:
                metric.BooleanValue = flag.Value;
                break;
            case MetricValue.Text text:
                metric.StringValue = text.Value;
                break;
            default:
                // Device đang nói rằng nó có một giá trị nhưng không đọc được. Không trường giá trị
                // nào được ghi, và chính điều đó khiến is_null là một lời khẳng định chứ không phải
                // một số 0.
                metric.IsNull = true;
                break;
        }
    }

    private static Payload Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return Payload.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException exception)
        {
            // Được bọc lại để các caller — gateway ở C08, ingestion ở C12 — chỉ cần một loại exception
            // để route sang `_error`, và không cần biết bên dưới là protobuf.
            throw new SparkplugDecodeException(
                $"The {payload.Length} bytes offered are not a Sparkplug B payload.", exception);
        }
    }

    private static MetricValue ReadValue(SparkplugMetric metric, DataType? declared, string metricName)
    {
        // Kiểm tra trước giá trị, vì is_null là device nói rằng nó có một giá trị nhưng không đọc
        // được. Một sensor bị lỏng sẽ báo cái này, và khi đó oneof trống một cách hợp lệ.
        if (metric.HasIsNull && metric.IsNull)
        {
            return MetricValue.Absent.Instance;
        }

        if (metric.ValueCase == ValueCase.None)
        {
            throw new SparkplugDecodeException(
                $"Metric '{metricName}' carries neither a value nor is_null. Those are different "
                + "statements and this payload makes neither.");
        }

        return declared is { } dataType
            ? ReadDeclared(metric, dataType, metricName)
            : ReadUndeclared(metric, metricName);
    }

    private static MetricValue ReadDeclared(SparkplugMetric metric, DataType dataType, string metricName) =>
        dataType switch
        {
            // Số nguyên có dấu đi trong int_value, một uint32 của protobuf, nên -1 tới nơi dưới dạng
            // 4294967295. Phép cast unchecked là cách diễn giải lại mà spec yêu cầu, không phải làm tròn.
            DataType.Int8 or DataType.Int16 or DataType.Int32 =>
                new MetricValue.Integral(unchecked((int)Require(metric, ValueCase.IntValue, dataType, metricName).IntValue)),

            DataType.Uint8 or DataType.Uint16 or DataType.Uint32 =>
                new MetricValue.Integral(Require(metric, ValueCase.IntValue, dataType, metricName).IntValue),

            DataType.Int64 =>
                new MetricValue.Integral(unchecked((long)Require(metric, ValueCase.LongValue, dataType, metricName).LongValue)),

            DataType.Uint64 =>
                new MetricValue.Integral(ToSignedOrThrow(Require(metric, ValueCase.LongValue, dataType, metricName).LongValue, metricName)),

            DataType.Float =>
                new MetricValue.Real(Require(metric, ValueCase.FloatValue, dataType, metricName).FloatValue),

            DataType.Double =>
                new MetricValue.Real(Require(metric, ValueCase.DoubleValue, dataType, metricName).DoubleValue),

            DataType.Boolean =>
                new MetricValue.Flag(Require(metric, ValueCase.BooleanValue, dataType, metricName).BooleanValue),

            DataType.String or DataType.Text or DataType.Uuid =>
                new MetricValue.Text(Require(metric, ValueCase.StringValue, dataType, metricName).StringValue),

            // DataSet, Template, Bytes, File, các array, các property set — và bất kỳ con số nào
            // không phải một datatype nào cả. Bị từ chối thay vì bị bỏ qua: một formation channel
            // không phát ra chúng, nên gặp phải một cái nghĩa là payload không phải thứ pipeline này
            // nghĩ nó là.
            _ => throw new SparkplugDecodeException(
                $"Metric '{metricName}' declares datatype {(uint)dataType}, which this decoder does "
                + "not read. M2 handles the scalar types only."),
        };

    private static MetricValue ReadUndeclared(SparkplugMetric metric, string metricName) =>
        metric.ValueCase switch
        {
            ValueCase.FloatValue => new MetricValue.Real(metric.FloatValue),
            ValueCase.DoubleValue => new MetricValue.Real(metric.DoubleValue),

            // Không có khai báo nghĩa là không có cách nào biết đây là Int32 hay UInt32, nên nó được
            // đọc đúng như đã ghi: unsigned. Chỉ đến được đây với một metric tự đặt tên mình, không
            // mang datatype, và chưa từng xuất hiện trong một birth — tức là một device mà nhà máy
            // không nên có.
            ValueCase.IntValue => new MetricValue.Integral(metric.IntValue),
            ValueCase.LongValue => new MetricValue.Integral(ToSignedOrThrow(metric.LongValue, metricName)),

            ValueCase.BooleanValue => new MetricValue.Flag(metric.BooleanValue),
            ValueCase.StringValue => new MetricValue.Text(metric.StringValue),

            _ => throw new SparkplugDecodeException(
                $"Metric '{metricName}' carries a {metric.ValueCase} value, which this decoder does "
                + "not read. M2 handles the scalar types only."),
        };

    private static SparkplugMetric Require(
        SparkplugMetric metric,
        ValueCase expected,
        DataType declared,
        string metricName)
    {
        if (metric.ValueCase != expected)
        {
            throw new SparkplugDecodeException(
                $"Metric '{metricName}' was declared {declared} but carries a {metric.ValueCase} on "
                + "the wire. The declaration and the payload disagree, and taking either one would be "
                + "a guess about which is right.");
        }

        return metric;
    }

    private static long ToSignedOrThrow(ulong value, string metricName) =>
        value <= long.MaxValue
            ? (long)value
            : throw new SparkplugDecodeException(
                $"Metric '{metricName}' carries {value}, which does not fit a signed 64-bit reading.");

    private static DateTimeOffset ReadTimestamp(SparkplugMetric metric, ulong? payloadTimestamp, string metricName)
    {
        // Ưu tiên timestamp riêng của metric trước, rồi mới tới payload. Một message gom các reading
        // được lấy ở nhiều thời điểm khác nhau — đó là toàn bộ lý do một metric có timestamp riêng —
        // và dồn chúng về timestamp của payload sẽ âm thầm căn chỉnh các mẫu chưa bao giờ đồng thời.
        var milliseconds = metric.HasTimestamp
            ? metric.Timestamp
            : payloadTimestamp ?? throw new SparkplugDecodeException(
                $"Metric '{metricName}' has no timestamp and neither does the payload carrying it. "
                + "device_timestamp is part of the natural key (docs/scope.md §7.2), so a reading "
                + "without one could never be deduplicated.");

        // Một PLC có đồng hồ hỏng không được phép làm sập ingestion bằng một ArgumentOutOfRangeException
        // từ đâu đó trong BCL. Đây là một message hỏng, và nó nhận cùng câu trả lời như bất kỳ cái nào khác.
        if (milliseconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new SparkplugDecodeException(
                $"Metric '{metricName}' is stamped {milliseconds} ms after the epoch, which is not a "
                + "representable instant.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }
}
