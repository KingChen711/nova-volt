using System.Diagnostics;
using MassTransit;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.FactoryModel;
using Nvm.FactoryModel.Commands;
using Nvm.FactoryModel.Storage;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Hai endpoint chỉ dùng khi phát triển, đưa một event thật lên bus.
/// </summary>
/// <remarks>
/// <para>
/// Chỉ đăng ký khi environment là Development, cùng quy tắc <c>DotEnvLoader</c> tuân theo. Một
/// endpoint publish event tuỳ ý theo yêu cầu là điều tốt để có trên laptop nhưng lại là một cánh
/// cửa mở toang trong nhà máy.
/// </para>
/// <para>
/// Chúng tồn tại vì bằng chứng về fan-out, retry và outage cho M1 phải đến từ một publisher nằm
/// ngoài process đang consume. Một test publish từ bên trong worker sẽ chỉ chạy qua thư viện bus
/// và bỏ qua đúng phần cần chứng minh: rằng một message vượt qua ranh giới process và đến được hai
/// queue.
/// </para>
/// </remarks>
internal static partial class DevBusEndpoints
{
    private const string LoggerName = "Nvm.Host.DevBus";

    /// <summary>
    /// Nói thẳng ra rằng <c>published</c> không giống <c>received</c>.
    /// </summary>
    /// <remarks>
    /// Một publish trả về mà không ném exception nghĩa là broker đã acknowledge frame, không có
    /// nghĩa là consumer nào đó sẽ thấy message. Đọc hai con số này như một là sai lầm mà lab này
    /// dựng ra để lộ mặt, nên câu trả lời phải mang theo cảnh báo đó.
    /// </remarks>
    private const string LostEventsNote =
        "Events lost = requested - what the consumers actually received. Count the consumer log.";

    /// <summary>Thời gian mặc định cho phép một lần publish trước khi bị tính là thất bại.</summary>
    /// <remarks>
    /// Một publish tới broker không có ở đó sẽ không tự thất bại nhanh — MassTransit giữ message lại
    /// trong lúc cố kết nối lại, đây chính là hành vi làm cho việc phục hồi liền mạch nhưng cũng làm
    /// một outage lab chạy mãi không dừng. Giới hạn mỗi lần thử biến "rồi cũng xong" thành một con số.
    /// </remarks>
    private static readonly TimeSpan DefaultPublishTimeout = TimeSpan.FromSeconds(2);

    public static WebApplication MapDevBusEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/dev/bus").WithTags("dev");

        group.MapPost("/activate-revision", ActivateRevisionAsync);
        group.MapPost("/burst", PublishBurstAsync);

        return app;
    }

    /// <summary>
    /// Dispatch command thật và publish bất kỳ event nào nó tạo ra.
    /// </summary>
    /// <remarks>
    /// Publish diễn ra ở đây, phía caller, chứ không phải bên trong handler. Handler được giữ sạch
    /// khỏi MassTransit (AGENTS.md K9), và chỗ nối mà outbox sẽ chiếm ở M6 lộ rõ ra: hiện tại thay
    /// đổi trạng thái và publish là hai bước rời nhau không có gì gắn kết, đúng là dual-write mà
    /// outage lab đang đo.
    /// </remarks>
    private static async Task<IResult> ActivateRevisionAsync(
        string site,
        int revision,
        ICommandDispatcher dispatcher,
        IPublishEndpoint publishEndpoint,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(LoggerName);

        var command = new ActivateFactoryModelRevisionCommand(
            ActivateFactoryModelRevisionCommand.KeyFor(site, revision),
            site,
            revision);

        FactoryModelRevisionActivated activated;

        try
        {
            activated = await dispatcher.DispatchAsync(command, cancellationToken);
        }
        catch (CommandValidationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (FactoryModelActivationException exception)
        {
            // 409, không phải 400. Request đúng định dạng nhưng câu trả lời vẫn là không, đó là một
            // chuyện khác mà caller cần xử lý.
            return Results.Conflict(new { error = exception.Message });
        }

        await publishEndpoint.Publish(activated, cancellationToken);

        Published(logger, activated.Revision, activated.SiteId, activated.EventId);

        return Results.Ok(new
        {
            activated.EventId,
            activated.SiteId,
            activated.Revision,
            activated.NodeCount,
            activated.OccurredAt,
        });
    }

    /// <summary>
    /// Publish một loạt event được đánh số và báo cáo bao nhiêu event đã lên được bus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Số thứ tự được mang trong <c>Revision</c>, nên mỗi event trong một loạt đều phân biệt được
    /// trong consumer log, và khoảng trống do outage để lại có thể đếm được thay vì phải ước lượng.
    /// Các event là contract <c>FactoryModelRevisionActivated</c> thật, đi qua đúng topology thật và
    /// đúng CloudEvents filter thật; phần chúng bỏ qua là command handler, vì handler từ chối đưa một
    /// nhà máy lùi lại và một loạt hai trăm lần activation không phải điều một nhà máy có thể làm.
    /// </para>
    /// <para>
    /// Nó trả về số đếm chứ không chỉ ghi log. Một con số chỉ tồn tại trong một dòng log là con số ai
    /// đó phải đi tìm, còn con số này đi thẳng vào <c>benchmarks.md</c>.
    /// </para>
    /// </remarks>
    private static async Task<IResult> PublishBurstAsync(
        string site,
        int count,
        int? delayMs,
        int? timeoutMs,
        IFactoryModelCatalog catalog,
        IPublishEndpoint publishEndpoint,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (count < 1)
        {
            return Results.BadRequest(new { error = "count must be at least 1." });
        }

        // Tài liệu mới nhất trên kệ. Endpoint này là một publisher cho chaos lab, không phải một lần
        // activation, nên "revision nào" chỉ cần là một revision có thật.
        var model = catalog.Find(catalog.LatestRevision)!;

        if (model.FindSite(site) is null)
        {
            return Results.BadRequest(new { error = $"The model document does not describe plant '{site}'." });
        }

        var logger = loggerFactory.CreateLogger(LoggerName);
        var delay = TimeSpan.FromMilliseconds(delayMs ?? 100);
        var timeout = timeoutMs is null ? DefaultPublishTimeout : TimeSpan.FromMilliseconds(timeoutMs.Value);
        var nodeCount = model.NodeCount;

        var published = 0;
        var failed = 0;
        int? firstFailure = null;
        int? lastFailure = null;
        var startedAt = Stopwatch.GetTimestamp();

        BurstStarting(logger, count, site, delay.TotalMilliseconds, timeout.TotalMilliseconds);

        for (var sequence = 1; sequence <= count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var activated = new FactoryModelRevisionActivated(
                // Suy ra, không phải tạo mới: cùng một lần chạy sẽ publish cùng id, nên một bản trùng
                // đến sau khi broker phục hồi vẫn nhận ra được là bản trùng (ADR-010).
                EventId: ActivateFactoryModelRevisionCommand.KeyFor(site, sequence).Value,
                OccurredAt: clock.GetUtcNow(),
                SiteId: site,
                Revision: sequence,
                NodeCount: nodeCount,
                EquipmentPathsAdded: [],
                EquipmentPathsRemoved: []);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(timeout);

            try
            {
                await publishEndpoint.Publish(activated, attempt.Token);
                published++;
            }
            catch (Exception exception)
            {
                // Bắt lại và đếm lại, không bao giờ nuốt lỗi. Một publish thất bại âm thầm là cách một
                // nhà máy biết về một outage từ khách hàng thay vì từ dashboard — và đó là một nửa
                // điều D4 yêu cầu phải cho thấy.
                failed++;
                firstFailure ??= sequence;
                lastFailure = sequence;

                PublishFailed(logger, sequence, timeout.TotalMilliseconds, exception);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        BurstFinished(logger, published, failed, count, (long)elapsed.TotalMilliseconds);

        return Results.Ok(new
        {
            site,
            requested = count,
            published,
            failed,
            firstFailure,
            lastFailure,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            note = LostEventsNote,
        });
    }

    // Sinh từ source. CA1873, mới trong .NET 10, từ chối một lệnh gọi mức Information mang nhiều hơn
    // một property: các argument bị boxed vào một array trước khi có gì hỏi xem mức log đó có bật
    // hay không. Generator phát ra kiểm tra IsEnabled trước.
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Published revision {Revision} for {SiteId} (ce_id {EventId})")]
    private static partial void Published(ILogger logger, int revision, string siteId, Guid eventId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Burst starting: {Count} events for {SiteId}, {DelayMs} ms apart, "
            + "{TimeoutMs} ms allowed per publish")]
    private static partial void BurstStarting(
        ILogger logger,
        int count,
        string siteId,
        double delayMs,
        double timeoutMs);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Burst: publish of sequence {Sequence} failed after {TimeoutMs} ms")]
    private static partial void PublishFailed(
        ILogger logger,
        int sequence,
        double timeoutMs,
        Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Burst finished: {Published} published, {Failed} failed, of {Count} requested "
            + "in {ElapsedMs} ms")]
    private static partial void BurstFinished(
        ILogger logger,
        int published,
        int failed,
        int count,
        long elapsedMs);
}
