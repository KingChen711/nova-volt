using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Identity;
using Nvm.Simulator.Faults;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.Simulator.Formation;

/// <summary>A formation line pretending to be an edge node with a cycler under it.</summary>
/// <remarks>
/// <para>
/// Everything here is a function of <b>process time</b> — how far the plant has got — and never of the
/// wall clock. That is what makes time compression honest: running a cycle a thousand times faster
/// changes how long the run takes and produces exactly the same measurements, because the samples are
/// taken at fixed points of process time either way.
/// </para>
/// <para>
/// The sequence number belongs here rather than to a channel. Sparkplug's <c>seq</c> is per edge node
/// and rolls at 256, and it is how a consumer notices it has missed a message — one counter per device
/// would make a gap in it meaningless.
/// </para>
/// <para>
/// Channels are staggered across the cycle rather than started together. A real line loads cells
/// continuously, so at any moment some channels are charging, some are resting and some are
/// discharging — and a line where all thousand step between stages at the same instant would produce a
/// traffic shape nothing downstream will ever see again.
/// </para>
/// </remarks>
public sealed class FormationLine
{
    /// <summary>Sparkplug rolls <c>seq</c> at this, and a consumer counts on the wrap.</summary>
    private const ulong SequenceWrap = 256;

    private const string BirthDeathSequenceMetric = "bdSeq";
    private const string RebirthControlMetric = "Node Control/Rebirth";

    private readonly FormationProfile _profile;
    private readonly ImmutableArray<FormationChannel> _channels;
    private readonly long[] _cycleNumbers;
    private readonly TimeSpan[] _driftOffsets;
    private readonly DateTimeOffset _startedAt;

    // Interlocked, and long rather than ulong so it can be. A new MQTT session is opened by the
    // publisher's reconnect loop, which is a different thread from the tick loop that composes
    // messages under this session - so the number moves on one thread and is read on another.
    // It cannot be taken under the publishing lock: the reconnect loop calls BeginSession before
    // the socket is open, and the tick loop can be holding that lock waiting for exactly that
    // socket.
    private long _birthDeathSequence;
    private readonly DeviceClockDrift _drift;

    private ulong _sequence;

    /// <summary>Creates the line and the channels under it.</summary>
    /// <param name="linePath">The line, which is what the edge node speaks for.</param>
    /// <param name="channelPaths">The channels the plant actually has, from the factory model.</param>
    /// <param name="profile">The cycle shape.</param>
    /// <param name="startedAt">The device clock at process time zero.</param>
    /// <param name="birthDeathSequence">The session number this run publishes under.</param>
    /// <param name="drift">Which channels have a wrong clock. Null means every clock is right.</param>
    /// <exception cref="ArgumentException">The path is not a line, or there are no channels.</exception>
    public FormationLine(
        EquipmentPath linePath,
        IEnumerable<EquipmentPath> channelPaths,
        FormationProfile profile,
        DateTimeOffset startedAt,
        ulong birthDeathSequence = 0,
        DeviceClockDrift? drift = null)
    {
        ArgumentNullException.ThrowIfNull(linePath);
        ArgumentNullException.ThrowIfNull(channelPaths);
        ArgumentNullException.ThrowIfNull(profile);

        if (linePath.Kind != FactoryNodeKind.Line)
        {
            throw new ArgumentException(
                $"'{linePath.Value}' is a {linePath.Kind}; an edge node speaks for a line.",
                nameof(linePath));
        }

        _channels = [.. channelPaths.Select(path => new FormationChannel(path, profile))];

        if (_channels.IsEmpty)
        {
            throw new ArgumentException(
                $"'{linePath.Value}' has no channels to simulate. The plant's model is the source of "
                + "that list, so an empty one means the line was decommissioned or never rolled out.",
                nameof(channelPaths));
        }

        Path = linePath;
        _profile = profile;
        _startedAt = startedAt;
        _birthDeathSequence = (long)birthDeathSequence;
        _drift = drift ?? DeviceClockDrift.None;
        _cycleNumbers = new long[_channels.Length];
        _driftOffsets = new TimeSpan[_channels.Length];

        for (var index = 0; index < _channels.Length; index++)
        {
            _cycleNumbers[index] = -1;
            _driftOffsets[index] = _drift.For(_channels[index].Path.Code);
        }

        DriftedDeviceCount = _driftOffsets.Count(offset => offset != TimeSpan.Zero);
    }

    /// <summary>The line this node speaks for.</summary>
    public EquipmentPath Path { get; }

    /// <summary>How many channels are under it.</summary>
    public int ChannelCount => _channels.Length;

    /// <summary>Every measurement the line has TAKEN. The left-hand side of D1's reconciliation.</summary>
    /// <remarks>
    /// <para>
    /// Unaffected by either fault, and that is the point. A duplicate is the same measurement sent
    /// twice and a drifted clock is the same measurement stamped wrongly — neither is a reading the
    /// channel took, so neither belongs on this side of the reconciliation.
    /// </para>
    /// <para>
    /// It moves in <see cref="Connect"/> and <see cref="Advance"/>, where the channels are read,
    /// and never later. This side of the reconciliation describes the <b>plant</b>; the right-hand
    /// side describes the database. Moving this one downstream of the transport would put both sides
    /// on the far side of the same loss, and the equality would hold across data that never arrived.
    /// </para>
    /// </remarks>
    public long MeasurementCount => _channels.Sum(channel => channel.MeasurementCount);

    /// <summary>How many channels are running on a clock that is wrong.</summary>
    public int DriftedDeviceCount { get; }

    /// <summary>The node coming online: an <c>NBIRTH</c>, then a <c>DBIRTH</c> for every channel.</summary>
    /// <param name="processElapsed">How far the plant has got when the node connects.</param>
    /// <remarks>
    /// The order is not decoration. A consumer that saw a <c>DBIRTH</c> before the node's own birth
    /// would have a device belonging to a session it has never heard of, which is the state C11 asks
    /// for a rebirth over.
    /// </remarks>
    public ImmutableArray<ComposedMessage> Connect(TimeSpan processElapsed)
    {
        var nodeClock = _startedAt + processElapsed;
        var messages = ImmutableArray.CreateBuilder<ComposedMessage>(_channels.Length + 1);

        // seq restarts at 0 for a birth, which is what tells a consumer that the count it was keeping
        // belongs to a session that has ended.
        _sequence = 0;

        // The node's own clock, never a drifted one. The drift belongs to a device's front panel, and
        // an edge node is a different box; stamping the node birth with a device's error would make
        // bdSeq itself arrive from the wrong hour and put C11's session matching out.
        messages.Add(new ComposedMessage(
            Message(
                SparkplugMessageType.NodeBirth,
                Path,
                SparkplugPayload.EncodeBirth(NodeBirthMetrics(nodeClock), _sequence, nodeClock)),
            // Nothing on either side of the reconciliation: an NBIRTH carries bdSeq and the rebirth
            // control metric, the gateway forwards neither, and no instrument measured either.
            Measurements: 0));

        for (var index = 0; index < _channels.Length; index++)
        {
            messages.Add(DeclareChannel(index, processElapsed, nodeClock));
        }

        return messages.DrainToImmutable();
    }

    /// <summary>Starts a new MQTT session and returns the <c>bdSeq</c> that identifies it.</summary>
    /// <remarks>
    /// Only a new connection may call this. A rebirth must NOT: a rebirth re-declares the node
    /// inside the session it is already in, and moving <c>bdSeq</c> there would tell the gateway
    /// the session had been replaced. The gateway acts on exactly that comparison - it ignores an
    /// NDEATH whose <c>bdSeq</c> names a session that is already over - so the number has to move
    /// once per connection and never otherwise, or a will delivered late kills the live session.
    /// </remarks>
    public ulong BeginSession() => (ulong)Interlocked.Increment(ref _birthDeathSequence);

    /// <summary>The session this line is currently publishing under.</summary>
    public ulong BirthDeathSequence => (ulong)Interlocked.Read(ref _birthDeathSequence);

    /// <summary>Moves the plant to a point in time and returns what the line publishes there.</summary>
    /// <param name="processElapsed">How far the plant has got.</param>
    /// <returns>
    /// A <c>DBIRTH</c> for every channel that has just taken a new cell, and a <c>DDATA</c> for every
    /// channel whose readings moved. Usually far fewer than one message per channel — that is
    /// report-by-exception doing its job.
    /// </returns>
    public ImmutableArray<ComposedMessage> Advance(TimeSpan processElapsed)
    {
        var nodeClock = _startedAt + processElapsed;
        var messages = ImmutableArray.CreateBuilder<ComposedMessage>();

        for (var index = 0; index < _channels.Length; index++)
        {
            if (CycleNumberAt(index, processElapsed) != _cycleNumbers[index])
            {
                messages.Add(DeclareChannel(index, processElapsed, nodeClock));
                continue;
            }

            var deviceClock = nodeClock + _driftOffsets[index];
            var changed = _channels[index].Sample(CycleElapsedAt(index, processElapsed), deviceClock);

            if (!changed.IsEmpty)
            {
                messages.Add(new ComposedMessage(
                    Message(
                        SparkplugMessageType.DeviceData,
                        _channels[index].Path,
                        SparkplugPayload.EncodeData(changed, NextSequence(), deviceClock)),
                    changed.Length));
            }
        }

        return messages.DrainToImmutable();
    }

    private static SparkplugMessage Message(SparkplugMessageType messageType, EquipmentPath path, byte[] payload) =>
        new(SparkplugTopic.For(path, messageType), [.. payload]);

    private ImmutableArray<DeviceReading> NodeBirthMetrics(DateTimeOffset deviceClock) =>
    [
        // bdSeq carries no alias, by specification: it has to be readable in an NDEATH published by
        // the broker as a last will, and a last will is composed before the birth that would have
        // assigned the alias.
        new DeviceReading(BirthDeathSequenceMetric, null, new MetricValue.Integral(Interlocked.Read(ref _birthDeathSequence)), deviceClock),
        new DeviceReading(RebirthControlMetric, null, new MetricValue.Flag(false), deviceClock),
    ];

    private ComposedMessage DeclareChannel(int index, TimeSpan processElapsed, DateTimeOffset nodeClock)
    {
        var cycleNumber = CycleNumberAt(index, processElapsed);

        _cycleNumbers[index] = cycleNumber;

        // The serial reads the node's clock and the readings read the device's. A wrong front-panel
        // clock does not re-engrave the cell sitting in the channel, and a fault that changed the
        // serial would be simulating a mislabelled cell rather than a late one — a different, much
        // louder failure, and not the one C13 has to classify.
        var deviceClock = nodeClock + _driftOffsets[index];

        // Read across the declaration rather than taken from the array it returns. A DBIRTH declares
        // six readings, but a REBIRTH declares the same six at the same instant — the same natural
        // keys, which the database stores once, so it adds nothing to the left-hand side. The channel
        // is the only thing that knows which of the two this was, and the difference is how it says
        // so. Taking readings.Length here instead would make an abandoned rebirth look like six lost
        // measurements that were never lost.
        var counted = _channels[index].MeasurementCount;

        var readings = _channels[index].Declare(
            CellSerialFor(index, cycleNumber, nodeClock),
            CycleElapsedAt(index, processElapsed),
            deviceClock);

        return new ComposedMessage(
            Message(
                SparkplugMessageType.DeviceBirth,
                _channels[index].Path,
                SparkplugPayload.EncodeBirth(readings, NextSequence(), deviceClock)),
            (int)(_channels[index].MeasurementCount - counted));
    }

    private TimeSpan Offset(int index) => _profile.CycleDuration * index / _channels.Length;

    private TimeSpan CycleElapsedAt(int index, TimeSpan processElapsed)
    {
        var total = processElapsed + Offset(index);

        return total - (_profile.CycleDuration * Math.Floor(total / _profile.CycleDuration));
    }

    private long CycleNumberAt(int index, TimeSpan processElapsed) =>
        (long)Math.Floor((processElapsed + Offset(index)) / _profile.CycleDuration);

    /// <summary>Builds the 16-character serial the cell in a channel was engraved with.</summary>
    /// <remarks>
    /// Layout from docs/scope.md §6.1, and checked against <see cref="SerialNumber"/> rather than
    /// trusted: a simulator quietly producing malformed serials would make every genealogy test
    /// downstream pass on data no engraver could have produced.
    /// <para>
    /// The shift letter comes off the UTC hour. Production day and shift are a plant's own calendar
    /// and belong to M3; using the plain hour here is a placeholder that is wrong in a stated way
    /// rather than in a hidden one.
    /// </para>
    /// <para>
    /// A pure function of the channel and the cycle, not a counter. A node reconnecting republishes a
    /// <c>DBIRTH</c> for a cell that is already in the channel, and a counter would give that same cell
    /// a second identity — which is the one thing a serial number is not allowed to do.
    /// </para>
    /// </remarks>
    private string CellSerialFor(int index, long cycleNumber, DateTimeOffset deviceClock)
    {
        // Cycles are the high part and channels the low part, so the thousand cells loaded across the
        // line in one pass get a thousand consecutive numbers, the way a plant issues them. The range
        // is 1-99999 because a serial has no cell zero: an engraver starts counting a shift at one.
        var sequence = (((cycleNumber * _channels.Length) + index) % 99_999) + 1;

        var shift = deviceClock.Hour switch
        {
            >= 6 and < 14 => 'A',
            >= 14 and < 22 => 'B',
            _ => 'C',
        };

        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{Path.SiteId}C{Path.Code}{deviceClock.Year % 10}{deviceClock.DayOfYear:000}{shift}{sequence:00000}");

        return SerialNumber.TryParse(value, out var serial)
            ? serial.Value
            : throw new InvalidOperationException(
                $"The simulator built '{value}', which is not a serial number this plant could engrave.");
    }

    private ulong NextSequence()
    {
        _sequence = (_sequence + 1) % SequenceWrap;

        return _sequence;
    }
}
