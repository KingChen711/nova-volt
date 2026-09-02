namespace Nvm.BusLab;

/// <summary>Các dòng công cụ đo này in ra trước khi bắt đầu lắng nghe.</summary>
/// <remarks>
/// Source-generated thay vì các lời gọi <c>logger.LogInformation(...)</c> trần trụi trong
/// <c>Program</c>. CA1873 từ chối một lời gọi cấp Information mang bất kỳ argument nào: giá trị bị
/// box vào một array trước khi bất cứ thứ gì kiểm tra xem level có được bật hay không. Top-level
/// statement không thể chứa một partial method <c>[LoggerMessage]</c>, nên các dòng này sống ở đây.
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
