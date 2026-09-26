using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;
using Nvm.Kernel.EventSourcing;

namespace Nvm.CommandStore;

/// <summary>Request chung của Command API từ M6: site chỉ để đối chiếu, key là tuỳ chọn (ADR-041).</summary>
public sealed record CommandRequest<TPayload>(
    string? IdempotencyKey, string SiteId,
    [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] TPayload Payload) where TPayload : class;

/// <summary>Chuyển HTTP → <see cref="DurableCommand"/> → dispatcher, và dịch lỗi hạ tầng thành mã ổn định.</summary>
public static class DurableCommandHttp
{
    private const string IdentityConflictMessage = "Idempotency identity or payload conflict.";

    /// <summary>Policy ghi: đúng một site_id, có sub, và thuộc một trong các role cho phép.</summary>
    public static IServiceCollection AddSiteWritePolicy(this IServiceCollection services, string name,
        params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthorizationBuilder().AddPolicy(name, policy => SiteScoped(policy).RequireRole(roles));
        return services;
    }

    /// <summary>Principal phải mang đúng một site hợp lệ và một actor.</summary>
    public static AuthorizationPolicyBuilder SiteScoped(AuthorizationPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RequireAuthenticatedUser().RequireAssertion(context =>
        {
            var sites = context.User.FindAll("site_id").ToArray();
            var actors = context.User.FindAll("sub").ToArray();
            return sites.Length == 1 && sites[0].Value.Length == 3 &&
                sites[0].Value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) &&
                actors.Length == 1 && actors[0].Value.Length is > 0 and <= 200;
        });
    }

    public static async Task<IResult> ExecuteAsync<TPayload>(CommandRequest<TPayload>? request,
        HttpContext context, ICommandDispatcher dispatcher, ILogger logger,
        Func<string, string, CommandRequest<TPayload>, DurableCommand> create,
        Func<string, int>? rejectionStatus, CancellationToken ct) where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(create);
        if (request?.Payload is null || string.IsNullOrWhiteSpace(request.SiteId) || request.OccurredAt == default)
        { return Result(400, DomainCommandResult.InvalidInput, "Yêu cầu sai định dạng."); }
        var site = context.User.FindFirst("site_id")?.Value ?? "";
        var actor = context.User.FindFirst("sub")?.Value ?? "";
        if (!string.Equals(site, request.SiteId, StringComparison.Ordinal))
        { return Result(403, "SITE_MISMATCH", "Site trong yêu cầu không khớp phiên đăng nhập."); }
        DurableCommand command;
        try
        { command = create(site, actor, request); }
        catch (ArgumentException)
        { return Result(400, DomainCommandResult.InvalidInput, "Yêu cầu sai định dạng."); }
        if (request.IdempotencyKey is { Length: > 0 } key &&
            (!Guid.TryParse(key, out var supplied) || supplied != command.IdempotencyKey.Value))
        { return Result(400, "KEY_MISMATCH", "Idempotency key không khớp submission."); }
        try
        {
            var outcome = await dispatcher.DispatchAsync<DomainCommandResult>(command, ct).ConfigureAwait(false);
            var status = outcome.Accepted ? 202 : rejectionStatus?.Invoke(outcome.ReasonCode) ?? 409;
            return Results.Json(outcome, statusCode: status);
        }
        catch (CommandValidationException validation)
        {
            return Result(400, DomainCommandResult.InvalidInput,
                string.Join(" ", validation.Failures.Select(failure => failure.Message)));
        }
        catch (EventConcurrencyException)
        { return Result(409, "CONCURRENT_MODIFICATION", "Dữ liệu vừa thay đổi. Gửi lại cùng submission."); }
        catch (EventIdentityConflictException)
        { return Result(409, "IDENTITY_CONFLICT", "Xung đột định danh yêu cầu."); }
        catch (InvalidOperationException error) when (
            string.Equals(error.Message, IdentityConflictMessage, StringComparison.Ordinal))
        { return Result(409, "IDENTITY_CONFLICT", "Cùng khoá nhưng nội dung khác với lần gửi trước."); }
        catch (SqlException error)
        {
            logger.LogError(error, "{Command} store unavailable for site {SiteId}", command.CommandType, site);
            return Result(503, "STORE_UNAVAILABLE", "Chưa xác nhận được kết quả. Thử lại cùng submission.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "{Command} failed for site {SiteId}", command.CommandType, site);
            return Result(500, "INTERNAL_ERROR", "Chưa xác nhận được kết quả. Thử lại cùng submission.");
        }
    }

    private static IResult Result(int status, string reason, string text) =>
        Results.Json(DomainCommandResult.Reject(reason, text), statusCode: status);
}
