using System.Collections.Immutable;
using System.Text.Json;
using Nvm.Contracts.Events.Grading;
using Nvm.Contracts.Queries;
using Nvm.Grading.Commands;
using Nvm.Grading.Entities;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;

namespace Nvm.Grading.Handlers;

/// <summary>Trạng thái lưu của một rule set.</summary>
public sealed record StoredRuleSet(GradingRuleSet RuleSet, string Status, string AuthoredBy);

/// <summary>Rule set trong transaction của command. Tìm rule set hiệu lực chỉ trả bản đã duyệt.</summary>
public interface IGradingRuleSetStore
{
    Task<StoredRuleSet?> LoadForUpdateAsync(string siteId, string ruleSetId, int version, CancellationToken cancellationToken);

    Task SaveDraftAsync(string siteId, GradingRuleSet ruleSet, string author, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>False nếu sản phẩm đã có rule set duyệt cùng ngày hiệu lực.</summary>
    Task<bool> ApproveAsync(string siteId, GradingRuleSet ruleSet, string approver, string sha256, DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<GradingRuleSet?> FindEffectiveAsync(string siteId, string productCode, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class GradingProcessor(IEventStore events, IGradingRuleSetStore ruleSets, IUnitExecutionContextReader units,
    TimeProvider clock) :
    ICommandHandler<DefineGradingRuleSetCommand, DomainCommandResult>,
    ICommandHandler<ApproveGradingRuleSetCommand, DomainCommandResult>,
    ICommandHandler<GradeUnitCommand, DomainCommandResult>,
    ICommandHandler<ReevaluateUnitCommand, DomainCommandResult>
{
    public const string StreamType = "unit-grading";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string StreamId(string serialNumber) => "grading:" + serialNumber;

    public async Task<DomainCommandResult> HandleAsync(DefineGradingRuleSetCommand command, CancellationToken cancellationToken)
    {
        var ruleSet = new GradingRuleSet(command.RuleSetId, command.Version, command.ProductCode, command.EffectiveFrom,
            command.Bins, command.Rejects.IsDefault ? [] : command.Rejects);
        if (ruleSet.Problems() is { Count: > 0 } problems)
        { return DomainCommandResult.Reject(GradingReasonCodes.InvalidRuleSet, string.Join(" ", problems)); }
        if (await ruleSets.LoadForUpdateAsync(command.SiteId, command.RuleSetId, command.Version, cancellationToken)
                .ConfigureAwait(false) is not null)
        { return DomainCommandResult.Reject(GradingReasonCodes.RuleSetExists, "Version này đã tồn tại; tạo version mới."); }
        await ruleSets.SaveDraftAsync(command.SiteId, ruleSet, command.ActorId, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, null, "Đã lưu bản nháp rule set.");
    }

    public async Task<DomainCommandResult> HandleAsync(ApproveGradingRuleSetCommand command, CancellationToken cancellationToken)
    {
        var stored = await ruleSets.LoadForUpdateAsync(command.SiteId, command.RuleSetId, command.Version, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        { return DomainCommandResult.Reject(GradingReasonCodes.RuleSetNotFound, "Không tìm thấy rule set."); }
        if (stored.Status == "Approved")
        { return DomainCommandResult.Reject(GradingReasonCodes.AlreadyApproved, "Rule set đã được duyệt."); }
        if (string.Equals(stored.AuthoredBy, command.ActorId, StringComparison.Ordinal))
        { return DomainCommandResult.Reject(GradingReasonCodes.SeparationOfDuties, "Người soạn không được tự duyệt rule set."); }
        var now = clock.GetUtcNow();
        var sha = stored.RuleSet.ContentSha256();
        if (!await ruleSets.ApproveAsync(command.SiteId, stored.RuleSet, command.ActorId, sha, now, cancellationToken)
                .ConfigureAwait(false))
        { return DomainCommandResult.Reject(GradingReasonCodes.EffectiveDateTaken, "Đã có rule set duyệt cùng ngày hiệu lực."); }
        var set = stored.RuleSet;
        var fact = new GradingRuleSetApproved(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            set.RuleSetId, set.Version, set.ProductCode, set.EffectiveFrom, set.Bins, set.Rejects, sha, stored.AuthoredBy,
            command.ActorId);
        var version = await events.AppendAsync(command.SiteId, $"ruleset:{set.RuleSetId}:{set.Version}", "grading-rule-set", 0,
            ImmutableArray.Create(DomainEventRecord.Create(fact, $"urn:grading-rule-set:{set.RuleSetId}:v{set.Version}",
                $"{command.SiteId}:{set.RuleSetId}", now)), cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(GradeUnitCommand command, CancellationToken cancellationToken)
    {
        var unit = await units.ReadForCommandAsync(command.SerialNumber, cancellationToken).ConfigureAwait(false);
        if (unit?.ProductCode is not { } product)
        { return DomainCommandResult.Reject(GradingReasonCodes.UnitNotFound, "Không tìm thấy cell."); }
        var ruleSet = await ruleSets.FindEffectiveAsync(command.SiteId, product, command.OccurredAt, cancellationToken)
            .ConfigureAwait(false);
        if (ruleSet is null)
        { return DomainCommandResult.Reject(GradingReasonCodes.NoRuleSet, "Sản phẩm chưa có rule set hiệu lực tại thời điểm đo."); }
        var stream = await events.ReadStreamAsync(command.SiteId, StreamId(command.SerialNumber), cancellationToken)
            .ConfigureAwait(false);
        var measurement = new GradingMeasurement(command.CapacityAh, command.OcvMillivolt, command.DcirMilliOhm,
            command.OcvDriftMillivolt);
        return await AppendAsync(command, product, ruleSet, measurement, command.IdempotencyKey.Value, Last(stream),
            stream?.Version ?? 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DomainCommandResult> HandleAsync(ReevaluateUnitCommand command, CancellationToken cancellationToken)
    {
        var stream = await events.ReadStreamAsync(command.SiteId, StreamId(command.SerialNumber), cancellationToken)
            .ConfigureAwait(false);
        if (Last(stream) is not { } last)
        { return DomainCommandResult.Reject(GradingReasonCodes.NotGraded, "Cell chưa được grade lần nào."); }
        var ruleSet = await ruleSets.FindEffectiveAsync(command.SiteId, last.ProductCode, command.OccurredAt, cancellationToken)
            .ConfigureAwait(false);
        if (ruleSet is null)
        { return DomainCommandResult.Reject(GradingReasonCodes.NoRuleSet, "Sản phẩm chưa có rule set hiệu lực."); }
        if (ruleSet.RuleSetId == last.RuleSetId && ruleSet.Version == last.RuleSetVersion)
        { return DomainCommandResult.Reject(GradingReasonCodes.SameRuleSet, "Cell đã được grade theo rule set này."); }
        var measurement = new GradingMeasurement(last.CapacityAh, last.OcvMillivolt, last.DcirMilliOhm, last.OcvDriftMillivolt);
        return await AppendAsync(command, last.ProductCode, ruleSet, measurement, last.MeasurementId, last,
            stream!.Version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DomainCommandResult> AppendAsync(DurableCommand command, string product, GradingRuleSet ruleSet,
        GradingMeasurement measurement, Guid measurementId, UnitGraded? previous, long expectedVersion,
        CancellationToken cancellationToken)
    {
        var serial = command switch
        {
            GradeUnitCommand grade => grade.SerialNumber,
            ReevaluateUnitCommand reevaluate => reevaluate.SerialNumber,
            _ => throw new ArgumentException("Unsupported grading command.", nameof(command))
        };
        var outcome = ruleSet.Evaluate(measurement);
        var now = clock.GetUtcNow();
        var fact = new UnitGraded(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, serial, product,
            ruleSet.RuleSetId, ruleSet.Version, outcome.BinCode, outcome.RejectCode, measurement.CapacityAh,
            measurement.OcvMillivolt, measurement.DcirMilliOhm, measurement.OcvDriftMillivolt, measurementId,
            previous?.EventId, command.ActorId);
        var version = await events.AppendAsync(command.SiteId, StreamId(serial), StreamType, expectedVersion,
            ImmutableArray.Create(DomainEventRecord.Create(fact, "urn:trace-unit:cell:" + serial,
                $"{command.SiteId}:{serial}", now)), cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, version,
            outcome.BinCode is { } bin ? $"Bin {bin}." : $"Loại: {outcome.RejectCode}.");
    }

    private static UnitGraded? Last(EventStream? stream)
    {
        if (stream is null || stream.Events.IsDefaultOrEmpty)
        { return null; }
        var last = stream.Events[^1];
        return JsonSerializer.Deserialize<UnitGraded>(last.PayloadJson, Json)
            ?? throw new InvalidDataException("Grading event payload is null.");
    }
}
