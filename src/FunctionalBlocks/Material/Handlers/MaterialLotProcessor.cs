using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nvm.Contracts.Events;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Ports;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.Material.Commands;
using Nvm.Material.Entities;

namespace Nvm.Material.Handlers;

/// <summary>Lot vật liệu trong transaction của command.</summary>
public interface IMaterialLotStore
{
    Task<MaterialLot?> LoadForUpdateAsync(string siteId, string lotId, CancellationToken cancellationToken);

    Task CreateAsync(string siteId, MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken);

    Task UpdateAsync(string siteId, MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Lot cùng vật liệu nhận trước lot này, đã release, chưa hết hạn, còn hàng — thứ FIFO bắt dùng trước.</summary>
    Task<MaterialLot?> OlderAvailableAsync(string siteId, MaterialLot lot, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<MaterialOverride>> OverridesAsync(string siteId, string lotId, CancellationToken cancellationToken);

    Task AddOverrideAsync(string siteId, MaterialOverride grant, string actorId, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class MaterialLotProcessor(IEventStore events, IMaterialLotStore lots, ISignatureVerifier signatures,
    TimeProvider clock) :
    ICommandHandler<ReceiveMaterialLotCommand, DomainCommandResult>,
    ICommandHandler<ReleaseMaterialLotCommand, DomainCommandResult>,
    ICommandHandler<OpenMaterialLotCommand, DomainCommandResult>,
    ICommandHandler<GrantMaterialOverrideCommand, DomainCommandResult>
{
    public const string StreamType = "material-lot";
    public const string LotNotFound = "LOT_NOT_FOUND";
    public const string LotExists = "LOT_EXISTS";
    public const string AlreadyOpened = "LOT_ALREADY_OPENED";
    public const string AlreadyReleased = "LOT_ALREADY_RELEASED";

    public static string StreamId(string lotId) => "lot:" + lotId;

    public static string OverrideContent(string lotId, string rule, string justification, DateTimeOffset validUntil) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', "MATERIAL-OVERRIDE", lotId, rule,
            justification, validUntil.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))));

    public async Task<DomainCommandResult> HandleAsync(ReceiveMaterialLotCommand command, CancellationToken cancellationToken)
    {
        if (await lots.LoadForUpdateAsync(command.SiteId, command.LotId, cancellationToken).ConfigureAwait(false) is not null)
        { return DomainCommandResult.Reject(LotExists, "Lot đã được nhận."); }
        var now = clock.GetUtcNow();
        var fact = new MaterialLotReceived(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, command.LotId,
            command.MaterialCode, command.Quantity, command.UnitOfMeasure, command.ExpiresAt, command.MaxExposureMinutes,
            command.SupplierLotId, command.ActorId);
        var version = await AppendAsync(command.SiteId, command.LotId, 0, fact, now, cancellationToken).ConfigureAwait(false);
        await lots.CreateAsync(command.SiteId, new MaterialLot(command.LotId, command.MaterialCode, command.Quantity,
            command.UnitOfMeasure, command.OccurredAt, command.ExpiresAt, command.MaxExposureMinutes, null, LotQuality.Pending,
            version), now, cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(ReleaseMaterialLotCommand command, CancellationToken cancellationToken)
    {
        var lot = await lots.LoadForUpdateAsync(command.SiteId, command.LotId, cancellationToken).ConfigureAwait(false);
        if (lot is null)
        { return DomainCommandResult.Reject(LotNotFound, "Không tìm thấy lot."); }
        if (lot.Quality == LotQuality.Released)
        { return DomainCommandResult.Reject(AlreadyReleased, "Lot đã được release."); }
        var now = clock.GetUtcNow();
        var fact = new MaterialLotReleased(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, lot.LotId,
            command.ActorId);
        var version = await AppendAsync(command.SiteId, lot.LotId, lot.StreamVersion, fact, now, cancellationToken)
            .ConfigureAwait(false);
        await lots.UpdateAsync(command.SiteId, lot with { Quality = LotQuality.Released, StreamVersion = version }, now,
            cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(OpenMaterialLotCommand command, CancellationToken cancellationToken)
    {
        var lot = await lots.LoadForUpdateAsync(command.SiteId, command.LotId, cancellationToken).ConfigureAwait(false);
        if (lot is null)
        { return DomainCommandResult.Reject(LotNotFound, "Không tìm thấy lot."); }
        if (lot.OpenedAt is not null)
        { return DomainCommandResult.Reject(AlreadyOpened, "Lot đã được mở bao."); }
        var now = clock.GetUtcNow();
        var fact = new MaterialLotOpened(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId, lot.LotId,
            command.ActorId);
        var version = await AppendAsync(command.SiteId, lot.LotId, lot.StreamVersion, fact, now, cancellationToken)
            .ConfigureAwait(false);
        await lots.UpdateAsync(command.SiteId, lot with { OpenedAt = command.OccurredAt, StreamVersion = version }, now,
            cancellationToken).ConfigureAwait(false);
        return DomainCommandResult.Ok(fact.EventId, version);
    }

    public async Task<DomainCommandResult> HandleAsync(GrantMaterialOverrideCommand command, CancellationToken cancellationToken)
    {
        var lot = await lots.LoadForUpdateAsync(command.SiteId, command.LotId, cancellationToken).ConfigureAwait(false);
        if (lot is null)
        { return DomainCommandResult.Reject(LotNotFound, "Không tìm thấy lot."); }
        var overrideId = "OVR-" + command.IdempotencyKey.Value.ToString("N")[..12].ToUpperInvariant();
        if (await signatures.VerifyAsync(command.SiteId, command.SignatureIds, "MaterialOverride", command.LotId,
                command.SignedContent, ["QaManager"], command.ActorId, cancellationToken).ConfigureAwait(false) is { } reason)
        { return DomainCommandResult.Reject(reason, "Override cần chữ ký QaManager khác người đề nghị."); }
        var now = clock.GetUtcNow();
        var fact = new MaterialOverrideGranted(command.IdempotencyKey.Value, command.OccurredAt, now, command.SiteId,
            overrideId, lot.LotId, command.Rule, command.Justification, command.SignatureIds, command.ValidUntil,
            command.ActorId);
        var version = await AppendAsync(command.SiteId, lot.LotId, lot.StreamVersion, fact, now, cancellationToken)
            .ConfigureAwait(false);
        await lots.UpdateAsync(command.SiteId, lot with { StreamVersion = version }, now, cancellationToken).ConfigureAwait(false);
        await lots.AddOverrideAsync(command.SiteId, new MaterialOverride(overrideId, lot.LotId, command.Rule, command.ValidUntil),
            command.ActorId, now, cancellationToken).ConfigureAwait(false);
        return new DomainCommandResult(true, DomainCommandResult.AcceptedCode, fact.EventId, version, overrideId);
    }

    private Task<long> AppendAsync(string siteId, string lotId, long expected, IDomainEvent fact, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        events.AppendAsync(siteId, StreamId(lotId), StreamType, expected, ImmutableArray.Create(
            DomainEventRecord.Create(fact, "urn:material-lot:" + lotId, $"{siteId}:{lotId}", now)), cancellationToken);
}
