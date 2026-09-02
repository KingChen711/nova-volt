using System.Globalization;
using MassTransit;
using Nvm.Bus.Topology;
using Nvm.Contracts.Events.Quality;

namespace Nvm.BusLab.Consumers;

/// <summary>Fail ở mọi message, một cách cố ý, để error queue có thể được chứng minh là hoạt động.</summary>
/// <param name="logger">Nơi các attempt được đánh số in ra.</param>
/// <remarks>
/// <para>
/// Một dead letter path mà chưa ai từng thấy một message rơi vào là một cấu hình, không phải một
/// đảm bảo. Consumer này tồn tại để đưa một message vào đó rồi đọc lại được, và nó được bật bằng một
/// biến môi trường để lần chạy lab thông thường vẫn sạch.
/// </para>
/// <para>
/// Nó in ra <b>số thứ tự</b> của attempt thay vì cùng một câu năm lần. Retry xảy ra bên trong một
/// delivery duy nhất, nên counter của broker chỉ cho thấy trạng thái cuối cùng — log là nơi duy nhất
/// năm attempt hiện ra được, và năm dòng giống hệt nhau thì không thể phân biệt với một dòng được in
/// bởi năm instance.
/// </para>
/// <para>
/// Đây cũng là lý do rõ ràng nhất khiến cả project sống trong <c>tools/</c>. Một consumer cố tình
/// throw là một công cụ đo, và đặt nó cạnh EdgeGateway với Ingestion sẽ khẳng định điều ngược lại.
/// </para>
/// </remarks>
[BusEndpoint("quality", "measurement-failing")]
public sealed class FailingMeasurementConsumer(ILogger<FailingMeasurementConsumer> logger)
    : IConsumer<MeasurementRecorded>
{
    private readonly ILogger<FailingMeasurementConsumer> _logger = logger;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Luôn luôn. Đó chính là mục đích của consumer này.</exception>
    public Task Consume(ConsumeContext<MeasurementRecorded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // GetRetryAttempt() đếm số lần retry, nên delivery đầu tiên báo về 0. In ra nó thành
        // "attempt 1 of 5" chính là ranh giới giữa bằng chứng cho D2 và một cuộc tranh cãi lệch một
        // đơn vị về việc năm nghĩa là năm lần chạy hay sáu.
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
