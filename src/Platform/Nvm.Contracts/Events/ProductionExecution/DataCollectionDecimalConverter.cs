using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nvm.Contracts.Events.ProductionExecution;

/// <summary>Giữ JSON number theo contract v1 dù transport đăng ký converter decimal thành string.</summary>
public sealed class DataCollectionDecimalConverter : JsonConverter<decimal>
{
    /// <inheritdoc />
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDecimal();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value);
    }
}
