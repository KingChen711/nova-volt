using System.Collections.Immutable;
using Google.Protobuf;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug.Topics;
using WireBatch = Nvm.Sparkplug.Transport.Generated.SparkplugIngressBatch;
using WireMessage = Nvm.Sparkplug.Transport.Generated.DecodedSparkplugMessage;
using WireReading = Nvm.Sparkplug.Transport.Generated.DecodedDeviceReading;

namespace Nvm.Sparkplug;

/// <summary>Serializes the protobuf body used on the gateway-to-ingestion HTTP conduit.</summary>
/// <remarks>
/// Generated protobuf types stay internal to this assembly. Both deployables work with NovaVolt
/// types and byte arrays, so changing code generation cannot leak across the boundary ADR-027 owns.
/// </remarks>
public static class SparkplugIngressBatchCodec
{
    /// <summary>Encodes one non-empty HTTP batch.</summary>
    public static byte[] Encode(IReadOnlyCollection<DecodedSparkplugMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("An ingestion batch must contain at least one message.", nameof(messages));
        }

        var batch = new WireBatch();

        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            batch.Messages.Add(ToWire(message));
        }

        return batch.ToByteArray();
    }

    /// <summary>Decodes and validates one non-empty HTTP batch.</summary>
    /// <exception cref="SparkplugIngressBatchException">The bytes do not describe a valid v1 batch.</exception>
    public static ImmutableArray<DecodedSparkplugMessage> Decode(ReadOnlySpan<byte> payload)
    {
        WireBatch batch;

        try
        {
            batch = WireBatch.Parser.ParseFrom(payload);
        }
        catch (InvalidProtocolBufferException exception)
        {
            throw new SparkplugIngressBatchException(
                $"The {payload.Length} bytes offered are not a gateway ingestion batch.",
                exception);
        }

        if (batch.Messages.Count == 0)
        {
            throw new SparkplugIngressBatchException("A gateway ingestion batch contains no messages.");
        }

        var messages = ImmutableArray.CreateBuilder<DecodedSparkplugMessage>(batch.Messages.Count);

        foreach (var message in batch.Messages)
        {
            messages.Add(FromWire(message));
        }

        return messages.DrainToImmutable();
    }

    private static WireMessage ToWire(DecodedSparkplugMessage message)
    {
        var wire = new WireMessage
        {
            SiteId = message.SiteId,
            EquipmentPath = message.EquipmentPath.Value,
            Topic = message.Topic.Value,
            MessageType = message.Topic.MessageType.Token(),
            GatewayTimestampUnixMs = message.GatewayTimestamp.ToUnixTimeMilliseconds(),
        };

        foreach (var reading in message.Readings)
        {
            wire.Readings.Add(ToWire(reading));
        }

        return wire;
    }

    private static WireReading ToWire(DeviceReading reading)
    {
        var wire = new WireReading
        {
            MetricName = reading.MetricName,
            DeviceTimestampUnixMs = reading.DeviceTimestamp.ToUnixTimeMilliseconds(),
        };

        if (reading.Alias is { } alias)
        {
            wire.Alias = alias;
        }

        switch (reading.Value)
        {
            case MetricValue.Real real:
                wire.RealValue = real.Value;
                break;
            case MetricValue.Integral integral:
                wire.IntegralValue = integral.Value;
                break;
            case MetricValue.Flag flag:
                wire.FlagValue = flag.Value;
                break;
            case MetricValue.Text text:
                wire.TextValue = text.Value;
                break;
            case MetricValue.Absent:
                wire.AbsentValue = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(reading),
                    reading.Value,
                    "The reading carries a metric value the v1 ingress contract does not define.");
        }

        return wire;
    }

    private static DecodedSparkplugMessage FromWire(WireMessage wire)
    {
        if (!SparkplugMessageTypes.TryParse(wire.MessageType, out var messageType))
        {
            throw new SparkplugIngressBatchException(
                $"Gateway message declares unknown Sparkplug type '{wire.MessageType}'.");
        }

        EquipmentPath equipmentPath;
        SparkplugTopic topic;

        try
        {
            equipmentPath = EquipmentPath.Parse(wire.EquipmentPath);
            topic = SparkplugTopic.Parse(wire.Topic);
        }
        catch (FormatException exception)
        {
            throw new SparkplugIngressBatchException(
                "Gateway message carries an invalid equipment path or Sparkplug topic.",
                exception);
        }

        if (topic.MessageType != messageType)
        {
            throw new SparkplugIngressBatchException(
                $"Gateway message says type '{wire.MessageType}', but topic '{wire.Topic}' says "
                + $"'{topic.MessageType.Token()}'.");
        }

        var readings = ImmutableArray.CreateBuilder<DeviceReading>(wire.Readings.Count);

        foreach (var reading in wire.Readings)
        {
            readings.Add(FromWire(reading));
        }

        try
        {
            return new DecodedSparkplugMessage(
                wire.SiteId,
                equipmentPath,
                topic,
                FromUnixMilliseconds(wire.GatewayTimestampUnixMs, "gateway_timestamp"),
                readings.DrainToImmutable());
        }
        catch (ArgumentException exception)
        {
            throw new SparkplugIngressBatchException(
                "Gateway message does not name one consistent site, equipment path and topic.",
                exception);
        }
    }

    private static DeviceReading FromWire(WireReading wire)
    {
        if (string.IsNullOrWhiteSpace(wire.MetricName))
        {
            throw new SparkplugIngressBatchException("A gateway reading carries no metric name.");
        }

        MetricValue value = wire.ValueCase switch
        {
            WireReading.ValueOneofCase.RealValue => new MetricValue.Real(wire.RealValue),
            WireReading.ValueOneofCase.IntegralValue => new MetricValue.Integral(wire.IntegralValue),
            WireReading.ValueOneofCase.FlagValue => new MetricValue.Flag(wire.FlagValue),
            WireReading.ValueOneofCase.TextValue => new MetricValue.Text(wire.TextValue),
            WireReading.ValueOneofCase.AbsentValue when wire.AbsentValue => MetricValue.Absent.Instance,
            WireReading.ValueOneofCase.AbsentValue => throw new SparkplugIngressBatchException(
                $"Reading '{wire.MetricName}' marks absent_value false; absence must be explicit."),
            _ => throw new SparkplugIngressBatchException(
                $"Reading '{wire.MetricName}' carries no value."),
        };

        return new DeviceReading(
            wire.MetricName,
            wire.HasAlias ? wire.Alias : null,
            value,
            FromUnixMilliseconds(wire.DeviceTimestampUnixMs, $"{wire.MetricName}.device_timestamp"));
    }

    private static DateTimeOffset FromUnixMilliseconds(long value, string field)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SparkplugIngressBatchException(
                $"Field '{field}' is stamped {value} ms after the Unix epoch, outside DateTimeOffset.",
                exception);
        }
    }
}
