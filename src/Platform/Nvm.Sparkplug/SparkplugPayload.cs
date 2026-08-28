using System.Collections.Immutable;
using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;
using SparkplugMetric = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric;
using ValueCase = Org.Eclipse.Tahu.Protobuf.Payload.Types.Metric.ValueOneofCase;

namespace Nvm.Sparkplug;

/// <summary>Turns Sparkplug B bytes into device readings.</summary>
/// <remarks>
/// <para>
/// The only door out of this assembly. <c>Org.Eclipse.Tahu.Protobuf</c> types are generated from a
/// specification we do not own (ADR-026), and letting them into a signature anywhere else would make
/// the whole system's shape depend on a file we are not allowed to edit.
/// </para>
/// <para>
/// Birth and data are separate methods because a payload does not say which it is — the message type
/// lives in the MQTT topic (<c>spBv1.0/{group}/DBIRTH/{node}/{device}</c>, docs/scope.md §7.1), and
/// C03 is what reads it. Guessing from the shape of the payload would work most of the time, which is
/// the worst frequency for a guess to work at.
/// </para>
/// <para>
/// Nothing here keeps state. The alias table is passed in and handed back, so the lifetime that
/// matters — one session of one edge node — stays the caller's to manage, and C11 is where that
/// becomes node state keyed on <c>bdSeq</c>.
/// </para>
/// </remarks>
public static class SparkplugPayload
{
    /// <summary>Decodes a birth payload: every metric names and declares itself.</summary>
    /// <param name="payload">The raw Sparkplug B bytes.</param>
    /// <returns>The readings the birth carried and the alias table it establishes.</returns>
    /// <exception cref="SparkplugDecodeException">
    /// The bytes are not a Sparkplug payload, or a metric is missing the name, datatype, value or
    /// timestamp that a birth is required to carry.
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

            // Strict rather than inferred from the value on the wire, and the reason is signedness:
            // Int32 and UInt32 both travel in int_value, so a birth that does not declare leaves
            // every reading of that metric ambiguous for the rest of the session.
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

        return new SparkplugBirth(readings.DrainToImmutable(), MetricAliasTable.From(definitions));
    }

    /// <summary>Decodes a data payload against the aliases its birth established.</summary>
    /// <param name="payload">The raw Sparkplug B bytes.</param>
    /// <param name="aliases">The table from the birth of this session of this node.</param>
    /// <returns>The readings that changed. Report-by-exception means this is normally a short list.</returns>
    /// <exception cref="UnknownMetricAliasException">
    /// A metric is identified only by an alias the table does not hold. Ask for a rebirth.
    /// </exception>
    /// <exception cref="SparkplugDecodeException">
    /// The bytes are not a Sparkplug payload, or a metric carries no identity, value or timestamp.
    /// </exception>
    public static ImmutableArray<DeviceReading> DecodeData(ReadOnlySpan<byte> payload, MetricAliasTable aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        var message = Parse(payload);
        var payloadTimestamp = message.HasTimestamp ? message.Timestamp : (ulong?)null;

        var readings = ImmutableArray.CreateBuilder<DeviceReading>(message.Metrics.Count);

        foreach (var metric in message.Metrics)
        {
            var alias = metric.HasAlias ? metric.Alias : (ulong?)null;
            var declared = metric.HasDatatype ? (DataType)metric.Datatype : (DataType?)null;
            string name;

            if (metric.HasName && !string.IsNullOrWhiteSpace(metric.Name))
            {
                // A named metric is self-describing, so it is accepted even mid-session. Its datatype
                // still comes from the birth when the wire omitted it — the birth remains the only
                // place the signedness of an integer was ever stated.
                name = metric.Name;

                if (alias is { } named && aliases.TryResolve(named, out var byAlias))
                {
                    if (!string.Equals(byAlias.Name, name, StringComparison.Ordinal))
                    {
                        // This reading could be filed correctly — it named itself. The next one under
                        // the same alias could not, and would go to the metric the stale table still
                        // remembers. Renumbering is announced with a birth, not smuggled in a DDATA.
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

    /// <summary>Encodes a birth: every metric declares its name, alias and type.</summary>
    /// <param name="readings">The current value of every metric the device offers.</param>
    /// <param name="sequence">The Sparkplug <c>seq</c> of this message.</param>
    /// <param name="timestamp">When the device assembled the payload.</param>
    /// <exception cref="ArgumentException">
    /// A reading has no value to declare a type from, or two readings share a name or an alias.
    /// </exception>
    /// <remarks>
    /// Written for the simulator in C05, and it is the only encoder in the repository. The captured
    /// fixtures in <c>tests/Fixtures/sparkplug/</c> deliberately do not come from it — a decoder
    /// checked against its own encoder agrees with itself even when both are wrong about the schema.
    /// </remarks>
    public static byte[] EncodeBirth(IReadOnlyList<DeviceReading> readings, ulong sequence, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var payload = NewPayload(sequence, timestamp);

        foreach (var reading in readings)
        {
            // A birth is a declaration, and there is nothing to declare about a metric whose type is
            // only knowable from a value it does not have. Sparkplug allows is_null at birth; this
            // system does not produce it, and refusing is better than inventing a type for it.
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

    /// <summary>Encodes a report-by-exception update: aliases and values, nothing else.</summary>
    /// <param name="readings">Only the metrics whose value moved.</param>
    /// <param name="sequence">The Sparkplug <c>seq</c> of this message.</param>
    /// <param name="timestamp">When the device assembled the payload.</param>
    /// <remarks>
    /// A reading that has an alias is written as the alias alone — no name, no datatype — because
    /// that is the whole economy of the protocol and because writing them anyway would make this
    /// encoder produce traffic no real device produces, which is the opposite of what a simulator is
    /// for. A reading with no alias falls back to its name.
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
            // Double and Int64 rather than the narrowest type that fits. A simulator that emitted
            // Float for one reading and Double for the next — because one happened to be round —
            // would produce a device whose declared type changes mid-session, which no real one does.
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
                // The device saying it has one and cannot read it. No value field is written, which is
                // what makes is_null a statement rather than a zero.
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
            // Wrapped so that callers — the gateway in C08, ingestion in C12 — have one exception
            // type to route to `_error`, and do not have to know that protobuf is underneath.
            throw new SparkplugDecodeException(
                $"The {payload.Length} bytes offered are not a Sparkplug B payload.", exception);
        }
    }

    private static MetricValue ReadValue(SparkplugMetric metric, DataType? declared, string metricName)
    {
        // Checked before the value, because is_null is the device saying it has one and cannot read
        // it. A sensor that has come loose reports this, and the oneof is then legitimately empty.
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
            // Signed integers travel in int_value, a protobuf uint32, so -1 arrives as 4294967295.
            // The unchecked cast is the reinterpretation the specification asks for, not a rounding.
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

            // DataSet, Template, Bytes, File, the arrays, the property sets — and any number that is
            // not a datatype at all. Refused rather than skipped: a formation channel does not emit
            // them, so meeting one means the payload is not what this pipeline thinks it is.
            _ => throw new SparkplugDecodeException(
                $"Metric '{metricName}' declares datatype {(uint)dataType}, which this decoder does "
                + "not read. M2 handles the scalar types only."),
        };

    private static MetricValue ReadUndeclared(SparkplugMetric metric, string metricName) =>
        metric.ValueCase switch
        {
            ValueCase.FloatValue => new MetricValue.Real(metric.FloatValue),
            ValueCase.DoubleValue => new MetricValue.Real(metric.DoubleValue),

            // No declaration means no way to know whether this is Int32 or UInt32, so it is read as
            // written: unsigned. Reachable only for a metric that names itself, carries no datatype,
            // and was never in a birth — which is a device the plant should not have.
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
        // Per-metric first, payload second. One message gathers readings taken at different instants —
        // that is the whole reason a metric has a timestamp of its own — and collapsing them onto the
        // payload's would quietly align samples that were never simultaneous.
        var milliseconds = metric.HasTimestamp
            ? metric.Timestamp
            : payloadTimestamp ?? throw new SparkplugDecodeException(
                $"Metric '{metricName}' has no timestamp and neither does the payload carrying it. "
                + "device_timestamp is part of the natural key (docs/scope.md §7.2), so a reading "
                + "without one could never be deduplicated.");

        // A PLC with a corrupted clock must not take ingestion down with an ArgumentOutOfRangeException
        // from somewhere in the BCL. It is a bad message, and it gets the same answer as any other.
        if (milliseconds > (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new SparkplugDecodeException(
                $"Metric '{metricName}' is stamped {milliseconds} ms after the epoch, which is not a "
                + "representable instant.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }
}
