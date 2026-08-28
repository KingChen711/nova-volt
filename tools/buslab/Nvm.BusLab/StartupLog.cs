namespace Nvm.BusLab;

/// <summary>The lines this instrument prints before it starts listening.</summary>
/// <remarks>
/// Source-generated rather than plain <c>logger.LogInformation(...)</c> calls in <c>Program</c>.
/// CA1873 refuses an Information-level call that carries an argument at all: the value is boxed into
/// an array before anything asks whether the level is switched on. Top-level statements cannot hold
/// a <c>[LoggerMessage]</c> partial method, so the lines live here.
/// </remarks>
internal static partial class StartupLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Bus lab starting with consumers: {Roles}.")]
    internal static partial void Starting(ILogger logger, string roles);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Failing consumer is ON. Everything it receives ends up in its error queue.")]
    internal static partial void FailingConsumerIsOn(ILogger logger);
}
