using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.ProductionExecution.Commands;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Endpoint POST ghi nhận kết quả đo. Xác thực + kiểm hình dạng → dispatch với durable outbox.</summary>
internal static partial class RecordDataCollectionEndpoint
{
    private const string LoggerName = "Nvm.ProductionExecution";

    // Store báo xung đột danh tính/payload bằng đúng thông điệp này (SqlIdempotencyStore); dịch sang 409.
    private const string IdentityConflictMessage = "Idempotency identity or payload conflict.";

    internal static async Task<IResult> HandleAsync(
        RecordDataCollectionRequest? request,
        HttpContext httpContext,
        ICommandDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "MALFORMED_REQUEST",
                "Yêu cầu sai định dạng.", "");
        }

        // Site và actor CHỈ lấy từ principal đã xác thực — không tin dữ liệu client khai. Policy đã bảo
        // đảm đúng một site_id ∈ {NV1, DE1} và có sub, nhưng vẫn lấy phòng thủ.
        var site = httpContext.User.FindFirst("site_id")?.Value ?? "";
        var actor = httpContext.User.FindFirst("sub")?.Value ?? "";

        if (!RecordDataCollectionCommandFactory.TryCreate(request, site, actor, out var command, out var error))
        {
            return error switch
            {
                RecordDataCollectionMappingError.SiteMismatch => Problem(
                    StatusCodes.Status403Forbidden, "SITE_MISMATCH",
                    "Site trong yêu cầu không khớp phiên đăng nhập.", request.IdempotencyKey ?? ""),
                RecordDataCollectionMappingError.KeyMismatch => Problem(
                    StatusCodes.Status400BadRequest, "KEY_MISMATCH",
                    "Idempotency key không khớp submission.", request.IdempotencyKey ?? ""),
                RecordDataCollectionMappingError.MalformedKey => Problem(
                    StatusCodes.Status400BadRequest, "MALFORMED_REQUEST",
                    "Idempotency key không hợp lệ.", request.IdempotencyKey ?? ""),
                _ => Problem(
                    StatusCodes.Status400BadRequest, "MALFORMED_REQUEST",
                    "SubmissionId phải là UUID hợp lệ.", request.IdempotencyKey ?? ""),
            };
        }

        var correlationId = command.IdempotencyKey.Value.ToString();

        RecordDataCollectionResult result;
        try
        {
            result = await dispatcher
                .DispatchAsync<RecordDataCollectionResult>(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CommandValidationException validation)
        {
            // Kiểm hình dạng nằm ngoài cùng, trước claim (ADR-023): không có claim nào bị chiếm ở đây.
            return Problem(StatusCodes.Status400BadRequest, "MALFORMED_REQUEST",
                string.Join(" ", validation.Failures.Select(failure => failure.Message)), correlationId);
        }
        catch (ArgumentException)
        {
            // Store bác khoá không khớp natural key: lỗi định danh, không lộ dữ liệu người khác.
            return Problem(StatusCodes.Status409Conflict, "IDENTITY_CONFLICT",
                "Xung đột định danh yêu cầu.", correlationId);
        }
        catch (InvalidOperationException conflict) when (
            string.Equals(conflict.Message, IdentityConflictMessage, StringComparison.Ordinal))
        {
            // Cùng khoá nhưng actor/payload khác: không trả outcome người khác, không ghi đè.
            return Problem(StatusCodes.Status409Conflict, "IDENTITY_CONFLICT",
                "Cùng khoá nhưng nội dung khác với lần gửi trước.", correlationId);
        }
        catch (SqlException exception)
        {
            // Không lộ connection string/stack: chỉ mã trạng thái và một câu tiếng Việt.
            StoreUnavailable(loggerFactory.CreateLogger(LoggerName), command.SiteId, exception);
            return Problem(StatusCodes.Status503ServiceUnavailable, "STORE_UNAVAILABLE",
                "Chưa xác nhận được kết quả. Thử lại cùng submission.", correlationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Unexpected(loggerFactory.CreateLogger(LoggerName), command.SiteId, exception);
            return Problem(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR",
                "Chưa xác nhận được kết quả. Thử lại cùng submission.", correlationId);
        }

        return Results.Json(result, statusCode: StatusCodes.Status200OK);
    }

    private static IResult Problem(int statusCode, string reasonCode, string reasonText, string correlationId) =>
        Results.Json(
            new RecordDataCollectionResult(
                Accepted: false,
                ReasonCode: reasonCode,
                ReasonText: reasonText,
                BlockingRules: [],
                AllowedNextActions: statusCode >= 500 ? ["RetrySameSubmission"] : DataCollectionNextActions.Standard,
                CorrelationId: correlationId),
            statusCode: statusCode);

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "record-data-collection: command store unavailable for {SiteId}")]
    private static partial void StoreUnavailable(ILogger logger, string siteId, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "record-data-collection: unexpected failure for {SiteId}")]
    private static partial void Unexpected(ILogger logger, string siteId, Exception exception);
}
