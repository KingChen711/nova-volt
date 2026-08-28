using System.Globalization;
using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Fails on every message, on purpose, so that the error queue can be shown to work.</summary>
/// <param name="logger">Where the numbered attempts are printed.</param>
/// <remarks>
/// <para>
/// A dead letter path that nobody has ever seen a message land in is a configuration, not a
/// guarantee. This consumer exists to put one there and let it be read back out, and it is switched
/// on by an environment variable so the ordinary lab run stays clean.
/// </para>
/// <para>
/// It prints the attempt <b>number</b> rather than the same sentence five times. Retries happen
/// inside a single delivery, so the broker's counters show only the final state — the log is the
/// only place the five attempts are visible, and five identical lines cannot be told apart from one
/// line printed by five instances.
/// </para>
/// <para>
/// This is also the clearest single reason the whole project lives in <c>tools/</c>. A consumer that
/// throws deliberately is an instrument, and putting it beside EdgeGateway and Ingestion would claim
/// otherwise.
/// </para>
/// </remarks>
[BusEndpoint("quality", "measurement-failing")]
public sealed class FailingMeasurementConsumer(ILogger<FailingMeasurementConsumer> logger)
    : IConsumer<MeasurementRecorded>
{
    private readonly ILogger<FailingMeasurementConsumer> _logger = logger;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always. That is the point of this consumer.</exception>
    public Task Consume(ConsumeContext<MeasurementRecorded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // GetRetryAttempt() counts retries, so the first delivery reports 0. Printing that as
        // "attempt 1 of 5" is the difference between evidence for D2 and an off-by-one argument
        // about whether five means five runs or six.
        var attempt = context.GetRetryAttempt() + 1;

        _logger.LogWarning(
            "measurement-failing attempt {Attempt} of {MaxAttempts} for {SignalCode} at {EquipmentPath} — "
            + "throwing on purpose",
            attempt,
            NvmRetryPolicy.MaxAttempts,
            context.Message.SignalCode,
            context.Message.EquipmentPath);

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"measurement-failing refuses {context.Message.SignalCode} on purpose "
            + $"(attempt {attempt} of {NvmRetryPolicy.MaxAttempts})."));
    }
}
