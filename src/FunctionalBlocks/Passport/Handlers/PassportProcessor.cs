using System.Collections.Immutable;
using Nvm.Contracts.Events.Passport;
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Passport.Commands;
using Nvm.Passport.Entities;

namespace Nvm.Passport.Handlers;

public static class PassportStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
}

/// <summary>Một version passport đã lưu.</summary>
public sealed record StoredPassport(PassportDocument Document, string ContentSha256, string Status, string PreparedBy);

public interface IPassportStore
{
    Task<BatteryModel?> ModelAsync(string siteId, string gtin, CancellationToken cancellationToken);

    Task<BatteryModel?> ModelForProductAsync(string siteId, string productCode, CancellationToken cancellationToken);

    Task AddModelAsync(string siteId, BatteryModel model, string actorId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<CarbonFootprint?> CarbonAsync(string siteId, string productCode, int year, CancellationToken cancellationToken);

    /// <summary>Thêm version mới của carbon record; trả version.</summary>
    Task<int> AddCarbonAsync(string siteId, CarbonFootprint carbon, string actorId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Version mới nhất (mọi trạng thái) của passport, khoá tới commit.</summary>
    Task<StoredPassport?> LatestForUpdateAsync(string siteId, string serialNumber, CancellationToken cancellationToken);

    Task<int?> LatestPublishedVersionAsync(string siteId, string serialNumber, CancellationToken cancellationToken);

    Task<StoredPassport?> LoadForUpdateAsync(string siteId, string serialNumber, int version, CancellationToken cancellationToken);

    Task AddDraftAsync(string siteId, PassportDocument document, string sha256, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken);

    Task PublishAsync(string siteId, string serialNumber, int version, ImmutableArray<string> signatureIds, string actorId,
        DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Bằng chứng về pack: trạng thái unit và genealogy. Adapter đọc từ traceability và read model trace.</summary>
public interface IPackEvidenceSource
{
    Task<(string UnitKind, PackEvidence Evidence)?> ReadAsync(string siteId, string serialNumber, CancellationToken cancellationToken);
}

public sealed class PassportProcessor(IEventStore events, IPassportStore passports, IPackEvidenceSource evidenceSource,
    ISignatureVerifier signatures, TimeProvider clock) :
    ICommandHandler<DefineBatteryModelCommand, DomainCommandResult>,
    ICommandHandler<RecordCarbonFootprintCommand, DomainCommandResult>,
    ICommandHandler<PreparePassportCommand, DomainCommandResult>,
    ICommandHandler<PublishPassportCommand, DomainCommandResult>
{
    public static readonly string[] PublishRoles = ["ComplianceOwner"];

    public static string SubjectId(string serialNumber, int version) => $"{serialNumber}:v{version}";

    public async Task<DomainCommandResult> HandleAsync(DefineBatteryModelCommand command, CancellationToken cancellationToken)
    {
        if (await passports.ModelAsync(command.SiteId, command.Gtin, cancellationToken).ConfigureAwait(false) is not null
            || await passports.ModelForProductAsync(command.SiteId, command.ProductCode, cancellationToken).ConfigureAwait(false) is not null)
        { return DomainCommandResult.Reject(PassportReasonCodes.ModelExists, "GTIN hoặc sản phẩm đã có model."); }
        var now = clock.GetUtcNow();
        await passports.AddModelAsync(command.SiteId, new BatteryModel(command.Gtin, command.ProductCode, command.Manufacturer,
            command.Chemistry, command.NominalEnergyKwh, command.ExpectedLifetimeCycles, command.RequiresPassport,
            command.MaterialCompositionJson, command.DismantlingUri, command.SafetyUri), command.ActorId, now, cancellationToken)
            .ConfigureAwait(false);
        var fact = new BatteryModelDefined(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, command.Gtin,
            command.ProductCode, command.Manufacturer, command.Chemistry, command.NominalEnergyKwh, command.ExpectedLifetimeCycles,
            command.RequiresPassport, command.ActorId);
        var version = await Append(command.SiteId, "battery-model:" + command.Gtin, "battery-model", 0, fact, now, cancellationToken)
            .ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(RecordCarbonFootprintCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var carbon = new CarbonFootprint(command.ProductCode, command.Year, 0, command.KgCo2ePerKwh,
            command.RecycledContentPercent, command.VerifiedBy);
        var version = await passports.AddCarbonAsync(command.SiteId, carbon, command.ActorId, now, cancellationToken)
            .ConfigureAwait(false);
        var fact = new CarbonFootprintRecorded(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            command.ProductCode, command.Year, version, command.KgCo2ePerKwh, command.RecycledContentPercent, command.VerifiedBy,
            command.ActorId);
        var stream = $"carbon:{command.ProductCode}:{command.Year}";
        var streamVersion = await Append(command.SiteId, stream, "carbon-footprint", version - 1, fact, now, cancellationToken)
            .ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, streamVersion, $"v{version}");
    }

    public async Task<DomainCommandResult> HandleAsync(PreparePassportCommand command, CancellationToken cancellationToken)
    {
        if (await evidenceSource.ReadAsync(command.SiteId, command.SerialNumber, cancellationToken).ConfigureAwait(false)
            is not { } found)
        { return DomainCommandResult.Reject(PassportReasonCodes.UnitNotFound, "Không tìm thấy pack."); }
        if (found.UnitKind != "Pack")
        { return DomainCommandResult.Reject(PassportReasonCodes.NotAPack, "Passport chỉ cấp cho pack."); }
        if (found.Evidence.QualityState is "Held" or "Scrapped")
        { return DomainCommandResult.Reject(PassportReasonCodes.UnitNotReleasable, $"Pack đang {found.Evidence.QualityState}."); }
        var model = await passports.ModelForProductAsync(command.SiteId, found.Evidence.ProductCode, cancellationToken)
            .ConfigureAwait(false);
        if (model is null)
        { return DomainCommandResult.Reject(PassportReasonCodes.ModelNotFound, $"Sản phẩm {found.Evidence.ProductCode} chưa có model."); }
        if (!model.RequiresPassport)
        {
            return DomainCommandResult.Reject(PassportReasonCodes.PassportNotRequired,
                $"Sản phẩm {model.ProductCode} không xuất EU: không cấp passport, chỉ giữ carbon footprint.");
        }
        var now = clock.GetUtcNow();
        DateOnly manufactured;
        try
        { manufactured = PassportProjection.ManufactureDate(command.SerialNumber, DateOnly.FromDateTime(now.UtcDateTime)); }
        catch (ArgumentException error)
        { return DomainCommandResult.Reject(PassportReasonCodes.InvalidSerial, error.Message); }
        var carbon = await passports.CarbonAsync(command.SiteId, model.ProductCode, manufactured.Year, cancellationToken)
            .ConfigureAwait(false);
        if (carbon is null)
        {
            return DomainCommandResult.Reject(PassportReasonCodes.MissingEvidence,
                $"Chưa có carbon footprint của {model.ProductCode} tại {command.SiteId} năm {manufactured.Year}.");
        }
        var latest = await passports.LatestForUpdateAsync(command.SiteId, command.SerialNumber, cancellationToken)
            .ConfigureAwait(false);
        if (latest is { Status: PassportStatuses.Draft })
        {
            return DomainCommandResult.Reject(PassportReasonCodes.DraftExists,
                $"Bản nháp v{latest.Document.Version} chưa công bố.");
        }
        var previous = await passports.LatestPublishedVersionAsync(command.SiteId, command.SerialNumber, cancellationToken)
            .ConfigureAwait(false);
        var document = PassportProjection.Build(command.SiteId, model, carbon, found.Evidence, manufactured,
            (latest?.Document.Version ?? 0) + 1, previous);
        var sha = document.ContentSha256();
        await passports.AddDraftAsync(command.SiteId, document, sha, command.ActorId, now, cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, null, document.Version, sha);
    }

    public async Task<DomainCommandResult> HandleAsync(PublishPassportCommand command, CancellationToken cancellationToken)
    {
        var stored = await passports.LoadForUpdateAsync(command.SiteId, command.SerialNumber, command.Version, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        { return DomainCommandResult.Reject(PassportReasonCodes.DraftNotFound, "Không tìm thấy bản nháp."); }
        if (stored.Status != PassportStatuses.Draft)
        { return DomainCommandResult.Reject(PassportReasonCodes.AlreadyPublished, "Version này đã công bố và không sửa được."); }
        if (await signatures.VerifyAsync(command.SiteId, command.SignatureIds, "Passport",
                SubjectId(command.SerialNumber, command.Version), stored.ContentSha256, PublishRoles, stored.PreparedBy,
                cancellationToken).ConfigureAwait(false) is { } reason)
        { return DomainCommandResult.Reject(reason, "Cần chữ ký ComplianceOwner trên đúng nội dung, khác người dựng bản nháp."); }
        var now = clock.GetUtcNow();
        await passports.PublishAsync(command.SiteId, command.SerialNumber, command.Version, command.SignatureIds, command.ActorId,
            now, cancellationToken).ConfigureAwait(false);
        var fact = new PassportPublished(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, command.SerialNumber,
            stored.Document.Gtin, command.Version, stored.Document.PreviousVersion, stored.ContentSha256, command.SignatureIds,
            command.ActorId);
        var stream = "passport:" + command.SerialNumber;
        var current = await events.ReadStreamAsync(command.SiteId, stream, cancellationToken).ConfigureAwait(false);
        var version = await Append(command.SiteId, stream, "passport", current?.Version ?? 0, fact, now, cancellationToken)
            .ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    private Task<long> Append(string siteId, string stream, string type, long expected, Contracts.Events.IDomainEvent fact,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, stream, type, expected,
            ImmutableArray.Create(DomainEventRecord.Create(fact, "urn:passport:" + stream, $"{siteId}:{stream}", now)), cancellationToken);
}
