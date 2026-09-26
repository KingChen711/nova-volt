using System.Globalization;
using System.Text;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.Kernel.Commands;

/// <summary>Kết quả chung của command ghi một fact: chấp nhận hoặc từ chối kèm mã lý do.</summary>
/// <param name="Accepted">Command đã tạo ra (hoặc trả lại) fact.</param>
/// <param name="ReasonCode">Mã máy đọc được; <c>ACCEPTED</c> khi chấp nhận.</param>
/// <param name="EventId">Event đã ghi, khi có.</param>
/// <param name="StreamVersion">Version của stream sau khi ghi, khi có.</param>
/// <param name="ReasonText">Câu tiếng Việt cho người vận hành; không chứa chi tiết kỹ thuật.</param>
public sealed record DomainCommandResult(bool Accepted, string ReasonCode, Guid? EventId, long? StreamVersion,
    string? ReasonText = null)
{
    public const string AcceptedCode = "ACCEPTED";
    public const string InvalidInput = "INVALID_INPUT";

    public static DomainCommandResult Ok(Guid eventId, long version) =>
        new(true, AcceptedCode, eventId, version, "Đã ghi nhận.");

    public static DomainCommandResult Reject(string reason, string? text = null) =>
        new(false, reason, null, null, text);
}

/// <summary>
/// Command bền vững dùng chung cho các FB từ M6: key UUIDv5 suy từ (site, loại command, submission).
/// </summary>
/// <remarks>
/// Site và actor do host lấy từ principal đã xác thực; client chỉ giữ submission ID khi gửi lại.
/// <see cref="CanonicalPayload"/> phải tất định để phát hiện cùng submission mang payload khác.
/// </remarks>
public abstract record DurableCommand : IDurableCommand, ICommand<DomainCommandResult>
{
    protected DurableCommand(string siteId, string actorId, string submissionId, DateTimeOffset occurredAt)
    {
        SiteId = siteId;
        ActorId = actorId;
        SubmissionId = submissionId;
        OccurredAt = occurredAt;
        IdempotencyKey = IdempotencyKey.FromNaturalKey(siteId, CommandType, submissionId);
    }

    public IdempotencyKey IdempotencyKey { get; }
    public abstract string CommandType { get; }
    public string SiteId { get; }
    public string ActorId { get; }
    public string SubmissionId { get; }
    public DateTimeOffset OccurredAt { get; }
    public abstract string CanonicalPayload { get; }

    /// <summary>Ghép các giá trị theo dạng độ-dài:giá-trị để không có hai payload khác nhau trùng chuỗi.</summary>
    protected string Canonical(params string?[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values.Append(OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
        {
            var text = value ?? "\u0000";
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }
        return builder.ToString();
    }

    protected static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    protected static string? Number(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture);
}
