using System.Globalization;
using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.BusProbe.Consumers;

/// <summary>Fails on every message, on purpose, so that the error queue can be shown to work.</summary>
/// <param name="logger">Where the numbered attempts are printed.</param>
/// <remarks>
/// <para>
/// A dead letter path that nobody has ever seen a message land in is a configuration, not a
/// guarantee. This consumer exists to put one there and let it be read back out, and it is switched
/// on by an environment variable so the ordinary probe run stays clean.
/// </para>
/// <para>
/// It prints the attempt <b>number</b> rather than the same sentence five times. Retries happen
/// inside a single delivery, so the broker's counters show only the final state — the log is the only
/// place the five attempts are visible, and five identical lines cannot be told apart from one line
/// printed by five instances.
/// </para>
/// </remarks>
[BusEndpoint("factory-model", "failing-probe")]
public sealed class FailingProbe(ILogger<FailingProbe> logger) : IConsumer<FactoryModelRevisionActivated>
{
    private readonly ILogger<FailingProbe> _logger = logger;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Always. That is the point of this consumer.</exception>
    public Task Consume(ConsumeContext<FactoryModelRevisionActivated> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // GetRetryAttempt() counts retries, so the first delivery reports 0. Printing that as
        // "attempt 1 of 5" is the difference between evidence for D2 and an off-by-one argument
        // about whether five means five runs or six.
        var attempt = context.GetRetryAttempt() + 1;

        _logger.LogWarning(
            "failing-probe attempt {Attempt} of {MaxAttempts} for revision {Revision} at {SiteId} — "
            + "throwing on purpose",
            attempt,
            NvmRetryPolicy.MaxAttempts,
            context.Message.Revision,
            context.Message.SiteId);

        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"failing-probe refuses revision {context.Message.Revision} on purpose "
            + $"(attempt {attempt} of {NvmRetryPolicy.MaxAttempts})."));
    }
}
