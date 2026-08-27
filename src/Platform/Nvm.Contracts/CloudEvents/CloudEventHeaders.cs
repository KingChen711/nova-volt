namespace Nvm.Contracts.CloudEvents;

/// <summary>
/// The transport header names that carry CloudEvents attributes alongside a message.
/// </summary>
/// <remarks>
/// <para>
/// CloudEvents defines bindings for HTTP (headers prefixed <c>ce-</c>), Kafka (<c>ce_</c>), AMQP
/// <b>1.0</b> (application properties prefixed <c>cloudEvents:</c>), MQTT and NATS. RabbitMQ speaks
/// AMQP <b>0-9-1</b>, which is not in that list — so there is no official binding to follow and any
/// choice here is a local convention.
/// </para>
/// <para>
/// The <c>ce_</c> prefix is borrowed from the Kafka binding, which is the closest in shape. Written
/// down here rather than assumed, so that in three years nobody goes looking for a specification that
/// says this. See <c>ADR-008</c>.
/// </para>
/// <para>
/// These are the attributes that can be derived from the event itself. The optional ones —
/// <c>subject</c>, <c>dataschema</c>, <c>correlationid</c>, <c>causationid</c>,
/// <c>partitionkey</c> — need a publisher to supply them and are <b>omitted</b> rather than written
/// empty: CloudEvents treats an absent attribute and a null one as different statements.
/// </para>
/// </remarks>
public static class CloudEventHeaders
{
    /// <summary>Prefix shared by every CloudEvents attribute header.</summary>
    public const string Prefix = "ce_";

    /// <summary>Specification version. Always <c>1.0</c>.</summary>
    public const string SpecVersion = Prefix + "specversion";

    /// <summary>Event identity. Equal to the event's <c>EventId</c>, and to the command's idempotency key.</summary>
    public const string Id = Prefix + "id";

    /// <summary>What happened: <c>com.novavolt.{context}.{event}.v{n}</c>.</summary>
    public const string Type = Prefix + "type";

    /// <summary>Who says so: <c>urn:novavolt:{site}:{application}</c>.</summary>
    public const string Source = Prefix + "source";

    /// <summary>When the system recorded the fact, RFC 3339 with an explicit offset.</summary>
    public const string Time = Prefix + "time";

    /// <summary>Encoding of the payload.</summary>
    public const string DataContentType = Prefix + "datacontenttype";
}
