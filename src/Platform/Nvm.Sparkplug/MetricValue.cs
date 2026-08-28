namespace Nvm.Sparkplug;

/// <summary>What one Sparkplug metric was carrying.</summary>
/// <remarks>
/// <para>
/// A closed set of cases rather than <c>object</c>. The alternative reads shorter and costs more
/// later: every consumer of a reading — the dedup key in C04, the telemetry insert in C12, the
/// canonical event in C14 — would have to test the runtime type and decide what to do when it is
/// none of the ones it expected. Written this way the compiler asks that question once, at the point
/// where a new case is added.
/// </para>
/// <para>
/// The cases follow the <b>wire</b>, not the plant. Sparkplug has twenty-odd datatypes and they
/// collapse into these five for our purposes: a quantity, a whole number, a two-state signal, text,
/// and "the device says there is nothing right now". What a value <i>means</i> — that
/// <c>Formation/Voltage</c> is volts — comes from the metric name and the equipment path, not from
/// here.
/// </para>
/// <para>
/// Datatypes M2 deliberately does not decode: <c>DataSet</c>, <c>Template</c>, <c>Bytes</c>,
/// <c>File</c>, the array types and the property-set types. A formation channel does not emit them,
/// and inventing a representation for data nothing produces is how a type ends up with a case nobody
/// can explain. Meeting one is an error, not a silent skip — see <see cref="SparkplugDecodeException"/>.
/// </para>
/// </remarks>
public abstract record MetricValue
{
    // Closed from outside the assembly: a sixth case added elsewhere would compile, and every switch
    // in the pipeline would fall through to its default arm without anybody being told.
    private protected MetricValue()
    {
    }

    /// <summary>A measured quantity — <c>Float</c> or <c>Double</c> on the wire.</summary>
    /// <param name="Value">The reading, widened to double.</param>
    /// <remarks>
    /// <c>Float</c> is widened rather than kept at 32 bits because <c>ts.process_signal.value</c> is
    /// <c>DOUBLE PRECISION</c> (docs/scope.md §8.3) and the widening is exact. Narrowing later would
    /// not be.
    /// </remarks>
    public sealed record Real(double Value) : MetricValue;

    /// <summary>A whole number — any of the <c>Int8</c>…<c>Int64</c> / <c>UInt8</c>…<c>UInt64</c> datatypes.</summary>
    /// <param name="Value">The reading, with the sign the declared datatype gives it.</param>
    /// <remarks>
    /// Signedness is the trap. Sparkplug puts <c>Int8</c>, <c>Int16</c> and <c>Int32</c> in the
    /// <c>int_value</c> field, which is a protobuf <c>uint32</c>, so −1 travels as 4294967295 and only
    /// the declared datatype says which of the two it is. That declaration arrives in the birth, which
    /// is a second reason an unknown alias cannot be guessed past.
    /// </remarks>
    public sealed record Integral(long Value) : MetricValue;

    /// <summary>A two-state signal — a door interlock, a heater on/off.</summary>
    /// <param name="Value">The state.</param>
    public sealed record Flag(bool Value) : MetricValue;

    /// <summary>Text — <c>String</c>, <c>Text</c> or <c>UUID</c> on the wire.</summary>
    /// <param name="Value">The text, never null.</param>
    /// <remarks>
    /// Not every metric is a process signal. <c>Formation/CellSerial</c> says which cell is in the
    /// channel, and that is an association rather than a measurement — C12 decides where it lands.
    /// </remarks>
    public sealed record Text(string Value) : MetricValue;

    /// <summary>The device set <c>is_null</c>: it has no value for this metric right now.</summary>
    /// <remarks>
    /// A distinct case rather than a dropped metric, and rather than a zero. A thermocouple that has
    /// come loose reports <c>is_null</c>; recording that as 0 °C puts a plausible number into a
    /// traceability record, and dropping the metric makes it look like report-by-exception decided
    /// nothing had changed. Both are wrong in the same direction — they turn a known unknown into a
    /// fact.
    /// </remarks>
    public sealed record Absent : MetricValue
    {
        /// <summary>The shared instance.</summary>
        /// <remarks>
        /// All absent values are equal, and at 5.000 msg/s the allocation is worth not making. Records
        /// compare structurally, so callers that build their own are indistinguishable from this one.
        /// </remarks>
        public static Absent Instance { get; } = new();
    }
}
