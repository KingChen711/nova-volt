using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Quality.Commands;

/// <summary>Giữ một lot, một cuộn (có thể chỉ một đoạn [from, to)) hoặc một unit. <c>TargetKind</c> ∈ {Lot, Roll, Unit}.</summary>
public sealed record PlaceHoldCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string TargetKind, string TargetId, decimal? SpanFromMeter, decimal? SpanToMeter, string ReasonCode, string? NcrId)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "PlaceHold";
    public override string CanonicalPayload => Canonical(TargetKind, TargetId, Number(SpanFromMeter), Number(SpanToMeter),
        ReasonCode, NcrId);
}

/// <summary>Nội bộ: chốt (hoặc bổ sung) danh sách unit hạ nguồn của hold từ read model genealogy.</summary>
public sealed record PlanHoldCascadeCommand(string Site, string HoldId, int Round, DateTimeOffset Time)
    : DurableCommand(Site, "system:hold-cascade", $"{HoldId}:plan:{Round}", Time)
{
    public override string CommandType => "PlanHoldCascade";
    public override string CanonicalPayload => Canonical(HoldId, Round.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Nội bộ: áp dụng một chunk; key theo (job, chunk) nên chạy lại là no-op.</summary>
public sealed record ApplyCascadeChunkCommand(string Site, string JobId, int ChunkIndex, DateTimeOffset Time)
    : DurableCommand(Site, "system:hold-cascade", $"{JobId}:chunk:{ChunkIndex}", Time)
{
    public override string CommandType => "ApplyCascadeChunk";
    public override string CanonicalPayload => Canonical(JobId, ChunkIndex.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Ghi một chữ ký điện tử. Host phải xác thực lại người ký (nhập lại mật khẩu) trước khi dispatch; mật khẩu không
/// bao giờ đi vào command. <c>SignerRole</c> phải là một role trong token của người ký.
/// </summary>
public sealed record SignCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string SubjectType, string SubjectId, string Meaning, string SignerRole, string ContentSha256, string? Disposition)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "Sign";
    public override string CanonicalPayload => Canonical(SubjectType, SubjectId, Meaning, SignerRole, ContentSha256, Disposition);
}

public sealed record ReleaseHoldCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string HoldId, ImmutableArray<string> SignatureIds)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ReleaseHold";
    public override string CanonicalPayload => Canonical([HoldId, .. SignatureIds.Order(StringComparer.Ordinal)]);
}

/// <summary>Quyết định MRB cho một NCR: UseAsIs, Rework, Scrap, Concession.</summary>
public sealed record ApplyDispositionCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string NcrId, string Disposition, ImmutableArray<string> SignatureIds)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "ApplyDisposition";
    public override string CanonicalPayload => Canonical([NcrId, Disposition, .. SignatureIds.Order(StringComparer.Ordinal)]);
}

public sealed class QualityCommandValidator : ICommandValidator<PlaceHoldCommand>, ICommandValidator<SignCommand>,
    ICommandValidator<ReleaseHoldCommand>, ICommandValidator<ApplyDispositionCommand>
{
    public IEnumerable<ValidationFailure> Validate(PlaceHoldCommand command)
    {
        if (command.TargetKind is not ("Lot" or "Roll" or "Unit"))
        { yield return new ValidationFailure(nameof(command.TargetKind), "TargetKind phải là Lot, Roll hoặc Unit."); }
        if (!Has(command.TargetId, 100) || !Has(command.ReasonCode, 64))
        { yield return new ValidationFailure(nameof(command.TargetId), "Đối tượng và lý do giữ là bắt buộc."); }
        var span = command.SpanFromMeter is not null || command.SpanToMeter is not null;
        if (span && (command.TargetKind != "Roll" || command.SpanFromMeter is not { } from ||
                command.SpanToMeter is not { } to || from < 0 || to <= from))
        { yield return new ValidationFailure("Span", "Chỉ cuộn mới có khoảng mét, và phải 0 ≤ from < to."); }
    }

    public IEnumerable<ValidationFailure> Validate(SignCommand command)
    {
        if (command.SubjectType is not ("QualityHold" or "NonConformance" or "RecipeVersion" or "Passport" or "MaterialOverride"))
        { yield return new ValidationFailure(nameof(command.SubjectType), "Loại đối tượng ký không hợp lệ."); }
        if (!Entities.SignatureChain.Meanings.Contains(command.Meaning, StringComparer.Ordinal))
        { yield return new ValidationFailure(nameof(command.Meaning), "Ý nghĩa chữ ký phải là Approved/Rejected/Reviewed/Witnessed."); }
        if (!Has(command.SubjectId, 100) || !Has(command.SignerRole, 50) || command.ContentSha256 is not { Length: 64 })
        { yield return new ValidationFailure(nameof(command.ContentSha256), "Đối tượng, vai trò và hash nội dung là bắt buộc."); }
    }

    public IEnumerable<ValidationFailure> Validate(ReleaseHoldCommand command) =>
        Has(command.HoldId, 64) && !command.SignatureIds.IsDefaultOrEmpty && command.SignatureIds.Length <= 10
            ? [] : [new ValidationFailure(nameof(command.SignatureIds), "Cần hold và 1–10 chữ ký.")];

    public IEnumerable<ValidationFailure> Validate(ApplyDispositionCommand command) =>
        Has(command.NcrId, 64) && command.Disposition is "UseAsIs" or "Rework" or "Scrap" or "Concession" &&
        !command.SignatureIds.IsDefaultOrEmpty
            ? [] : [new ValidationFailure(nameof(command.Disposition), "Cần NCR, quyết định hợp lệ và chữ ký.")];

    private static bool Has(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}

/// <summary>Ghi một subgroup SPC (ví dụ 5 lần cân coating weight cùng thời điểm).</summary>
public sealed record RecordSpcSampleCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string Characteristic, string SubgroupId, ImmutableArray<decimal> Values)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RecordSpcSample";
    public override string CanonicalPayload => Canonical([Characteristic, SubgroupId, .. Values.Select(v => Number(v))]);
}

public sealed class SpcSampleValidator : ICommandValidator<RecordSpcSampleCommand>
{
    public IEnumerable<ValidationFailure> Validate(RecordSpcSampleCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Characteristic) || command.Characteristic.Length > 64 ||
            string.IsNullOrWhiteSpace(command.SubgroupId) || command.SubgroupId.Length > 64)
        { yield return new ValidationFailure(nameof(command.Characteristic), "Đặc tính và subgroup là bắt buộc."); }
        if (command.Values.IsDefault || command.Values.Length is < 2 or > 10)
        { yield return new ValidationFailure(nameof(command.Values), "Subgroup cần 2–10 giá trị."); }
    }
}
