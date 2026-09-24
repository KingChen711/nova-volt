using System.Diagnostics.CodeAnalysis;

namespace Nvm.ProductionExecution.Commands;

/// <summary>Vì sao một request không dựng được thành command — quyết ngay, TRƯỚC khi claim.</summary>
public enum RecordDataCollectionMappingError
{
    /// <summary>Không có lỗi; command đã dựng.</summary>
    None = 0,

    /// <summary>Site trong body không khớp site của principal đã xác thực.</summary>
    SiteMismatch,

    /// <summary>Khoá client gửi không khớp khoá suy ra từ (site, commandType, submissionId).</summary>
    KeyMismatch,

    /// <summary>SubmissionId không phải UUID dạng <c>D</c> chữ thường.</summary>
    MalformedSubmissionId,

    /// <summary>IdempotencyKey không phải một GUID hợp lệ.</summary>
    MalformedKey,
}

/// <summary>
/// Dựng <see cref="RecordDataCollectionCommand"/> từ request, lấy site/actor CHỈ từ principal.
/// </summary>
/// <remarks>
/// Đây là "auth mapper" của scope §3.4: nó chặn site mismatch và khoá sai <b>trước</b> claim, nên một
/// command hỏng không bao giờ chiếm được khoá của người khác. Kiểm hình dạng field còn lại là việc của
/// <see cref="RecordDataCollectionValidator"/> (cũng chạy trước claim, ngoài cùng pipeline).
/// </remarks>
public static class RecordDataCollectionCommandFactory
{
    /// <summary>Dựng command, hoặc nêu lý do cụ thể để tầng HTTP map ra đúng mã lỗi.</summary>
    /// <param name="request">Envelope client gửi.</param>
    /// <param name="authenticatedSiteId">Site của principal (đúng một site_id).</param>
    /// <param name="actorId">Danh tính người thực hiện, từ principal.</param>
    /// <param name="command">Command dựng được khi trả về <see cref="RecordDataCollectionMappingError.None"/>.</param>
    /// <param name="error">Lý do không dựng được.</param>
    public static bool TryCreate(
        RecordDataCollectionRequest request,
        string authenticatedSiteId,
        string actorId,
        [NotNullWhen(true)] out RecordDataCollectionCommand? command,
        out RecordDataCollectionMappingError error)
    {
        ArgumentNullException.ThrowIfNull(request);
        command = null;

        var payload = request.Payload;
        if (payload is null)
        {
            error = RecordDataCollectionMappingError.MalformedSubmissionId;
            return false;
        }

        // Site chỉ lấy từ principal; body chỉ được phép trùng, không được phép khác. Khác nhau là một
        // nỗ lực ghi sang site khác, và K3 chặn nó ở đây trước khi chạm database.
        if (!string.Equals(request.SiteId, authenticatedSiteId, StringComparison.Ordinal))
        {
            error = RecordDataCollectionMappingError.SiteMismatch;
            return false;
        }

        // submissionId phải là UUID dạng D chữ thường: parse rồi so lại với chính chuỗi chuẩn hoá, nên
        // 'B' hoa hay dạng có ngoặc đều bị bác — một cách viết thứ hai của cùng id sẽ suy ra khoá khác.
        if (string.IsNullOrEmpty(payload.SubmissionId)
            || !Guid.TryParseExact(payload.SubmissionId, "D", out var submissionGuid)
            || !string.Equals(payload.SubmissionId, submissionGuid.ToString("D"), StringComparison.Ordinal))
        {
            error = RecordDataCollectionMappingError.MalformedSubmissionId;
            return false;
        }

        var derivedKey = RecordDataCollectionCommand.KeyFor(authenticatedSiteId, payload.SubmissionId);
        if (!string.IsNullOrEmpty(request.IdempotencyKey))
        {
            if (!Guid.TryParse(request.IdempotencyKey, out var providedKey))
            {
                error = RecordDataCollectionMappingError.MalformedKey;
                return false;
            }

            if (providedKey != derivedKey.Value)
            {
                error = RecordDataCollectionMappingError.KeyMismatch;
                return false;
            }
        }

        command = new RecordDataCollectionCommand(
            derivedKey,
            authenticatedSiteId,
            actorId,
            payload.SubmissionId,
            request.OccurredAt,
            payload.Serial,
            payload.OperationRunId,
            payload.StepCode,
            payload.EquipmentPath,
            payload.SignalCode,
            payload.Value,
            payload.UnitOfMeasure);
        error = RecordDataCollectionMappingError.None;
        return true;
    }
}
