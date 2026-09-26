using System.Collections.Immutable;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Queries;
using Nvm.Material.Commands;
using Nvm.Material.Handlers;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Handlers;
using Nvm.Traceability.Commands;
using Nvm.Traceability.Entities;
using Nvm.Traceability.Handlers;
using Nvm.Traceability.Ports;
using Nvm.UnitTests.TestSupport;

namespace Nvm.UnitTests.Traceability;

public sealed class GenealogyCommandTests
{
    private const string Site = "NV1";
    private const string Cell = "NV1CL16269A00001";
    private const string Module = "NV1MM16269A00001";
    private const string OtherModule = "NV1MM16269A00002";
    private const string Pack = "NV1PP16269A00001";
    private static readonly DateTimeOffset At = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assemble_ThenAssembleElsewhere_IsRejectedUntilRemoved()
    {
        var fixture = new Fixture();
        (await fixture.Genealogy.AssembleAsync(Assemble("a1", Cell, Module), Ct)).Accepted.ShouldBeTrue();
        (await fixture.Genealogy.AssembleAsync(Assemble("a2", Cell, OtherModule), Ct)).ReasonCode
            .ShouldBe(GenealogyReasonCodes.AlreadyAssembled);
        (await fixture.Genealogy.RemoveAsync(Remove("r1", Cell, OtherModule), Ct)).ReasonCode
            .ShouldBe(GenealogyReasonCodes.ParentMismatch);
        (await fixture.Genealogy.RemoveAsync(Remove("r2", Cell, Module), Ct)).Accepted.ShouldBeTrue();
        (await fixture.Genealogy.AssembleAsync(Assemble("a3", Cell, OtherModule), Ct)).Accepted.ShouldBeTrue();

        var membership = UnitMembership.Replay(Site, Cell,
            await fixture.Events.ReadStreamAsync(Site, UnitMembership.StreamId(Cell), Ct));
        membership.ParentSerialNumber.ShouldBe(OtherModule);
        membership.Version.ShouldBe(3);
    }

    [Theory]
    [InlineData(Module, Cell)]       // module không nằm trong cell
    [InlineData(Pack, Module)]       // pack không nằm trong module
    [InlineData(Cell, Cell + "X")]   // không thể tự chứa (serial sai định dạng bị bắt trước)
    public async Task Assemble_WrongHierarchy_IsRejected(string child, string parent)
    {
        var fixture = new Fixture();
        var result = await fixture.Genealogy.AssembleAsync(Assemble("bad", child, parent), Ct);
        result.Accepted.ShouldBeFalse();
        result.ReasonCode.ShouldBeOneOf(GenealogyReasonCodes.InvalidHierarchy, UnitReasonCodes.InvalidSerial);
    }

    [Fact]
    public async Task Assemble_HeldChildOrMissingParent_IsRejectedWithReason()
    {
        var fixture = new Fixture();
        fixture.Quality[Cell] = new UnitQualityFacet("Held", "QUALITY_HOLD");
        (await fixture.Genealogy.AssembleAsync(Assemble("held", Cell, Module), Ct)).ReasonCode.ShouldBe("QUALITY_HOLD");
        fixture.Quality.Remove(Cell);
        fixture.Units.Remove(Module);
        (await fixture.Genealogy.AssembleAsync(Assemble("orphan", Cell, Module), Ct)).ReasonCode
            .ShouldBe(GenealogyReasonCodes.ParentNotFound);
        fixture.Events.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Correction_ReplacesTheRecordedParent_OnlyWhenTheWrongParentMatches()
    {
        var fixture = new Fixture();
        await fixture.Genealogy.AssembleAsync(Assemble("a1", Cell, Module), Ct);
        (await fixture.Genealogy.CorrectAsync(Correct("c1", OtherModule, Module), Ct)).ReasonCode
            .ShouldBe(GenealogyReasonCodes.ParentMismatch);
        fixture.Quality[Cell] = new UnitQualityFacet("Held", "QUALITY_HOLD");   // sửa hồ sơ vẫn được khi đang giữ
        (await fixture.Genealogy.CorrectAsync(Correct("c2", Module, OtherModule), Ct)).Accepted.ShouldBeTrue();
        UnitMembership.Replay(Site, Cell, await fixture.Events.ReadStreamAsync(Site, UnitMembership.StreamId(Cell), Ct))
            .ParentSerialNumber.ShouldBe(OtherModule);
    }

    [Fact]
    public async Task ConsumeMaterial_RequiresSpanForRollsOnly_AndAUsableUnit()
    {
        var events = new MemoryEventStore();
        var units = new Units();
        var handler = new ConsumeMaterialHandler(events, units, new Lots(), new NoHolds(), TimeProvider.System);
        (await handler.HandleAsync(Consume("roll-no-span", "Roll", null, null), Ct)).ReasonCode
            .ShouldBe(MaterialReasonCodes.InvalidSpan);
        (await handler.HandleAsync(Consume("lot-with-span", "Lot", 1m, 2m), Ct)).ReasonCode
            .ShouldBe(MaterialReasonCodes.InvalidSpan);
        (await handler.HandleAsync(Consume("missing", "Lot", null, null), Ct)).ReasonCode
            .ShouldBe(MaterialReasonCodes.UnitNotFound);
        units.Context = new UnitExecutionContext(Site, Cell, "Cell", "run", "STACK", "eq", "Running", "Pending", "WO");
        (await handler.HandleAsync(Consume("ok", "Roll", 1250m, 1250.82m), Ct)).Accepted.ShouldBeTrue();
        (await handler.HandleAsync(Consume("ok-2", "Lot", null, null), Ct)).StreamVersion.ShouldBe(2);
        units.Context = units.Context with { QualityState = "Held" };
        (await handler.HandleAsync(Consume("held", "Lot", null, null), Ct)).ReasonCode.ShouldBe(MaterialReasonCodes.QualityHold);
    }

    [Fact]
    public async Task RollCoated_OverlapOnOneSideIsRejected_AndARollIsCoatedOnce()
    {
        var events = new MemoryEventStore();
        var handler = new RecordRollCoatedHandler(events, TimeProvider.System);
        ImmutableArray<RollSegment> overlapping =
            [new("A", 0m, 100m, "S", "F", "R", "E"), new("A", 99m, 120m, "S", "F", "R", "E")];
        (await handler.HandleAsync(new RecordRollCoatedCommand(Site, "op", "s1", At, "ROLL-1", overlapping), Ct))
            .ReasonCode.ShouldBe(RollReasonCodes.OverlappingSegments);
        ImmutableArray<RollSegment> sides =
            [new("A", 0m, 100m, "S", "F", "R", "E"), new("B", 50m, 150m, "S", "F", "R", "E"), new("A", 100m, 150m, "S", "F", "R", "E")];
        (await handler.HandleAsync(new RecordRollCoatedCommand(Site, "op", "s2", At, "ROLL-1", sides), Ct))
            .Accepted.ShouldBeTrue();
        (await handler.HandleAsync(new RecordRollCoatedCommand(Site, "op", "s3", At, "ROLL-1", sides), Ct))
            .ReasonCode.ShouldBe(RollReasonCodes.AlreadyCoated);
    }

    private static AssembleUnitCommand Assemble(string submission, string child, string parent) =>
        new(Site, "op", submission, child, At, parent, "S01", "run-1");

    private static RemoveUnitCommand Remove(string submission, string child, string parent) =>
        new(Site, "op", submission, child, At, parent, "REWORK", "run-2");

    private static CorrectGenealogyCommand Correct(string submission, string wrong, string right) =>
        new(Site, "qa", submission, Cell, At, wrong, right, "S07", "Quét nhầm module");

    private static ConsumeMaterialCommand Consume(string submission, string kind, decimal? from, decimal? to) =>
        new(Site, "op", submission, At, Cell, "LOT-1", kind, "ANODE", 1m, "m", from, to, "run-1");

    private sealed class Fixture : IUnitRegistry, IUnitQualityFacet
    {
        public MemoryEventStore Events { get; } = new();
        public HashSet<string> Units { get; } = [Cell, Module, OtherModule, Pack];
        public Dictionary<string, UnitQualityFacet> Quality { get; } = [];
        public GenealogyCommandProcessor Genealogy => new(Events, this, this, TimeProvider.System);

        public Task<bool> ExistsAsync(string siteId, string serialNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Units.Contains(serialNumber));

        public Task<UnitQualityFacet> ReadForCommandAsync(string siteId, string serialNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Quality.GetValueOrDefault(serialNumber, UnitQualityFacet.Pending));

        public Task QuarantineForIncidentAsync(string siteId, string serialNumber, Guid incidentEventId,
            string reasonCode, string actorId, DateTimeOffset occurredAt, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoHolds : Nvm.Contracts.Ports.IMaterialHoldCheck
    {
        public Task<string?> ActiveHoldAsync(string siteId, string lotKind, string lotId, decimal? spanFromMeter,
            decimal? spanToMeter, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>Kho lot trong RAM: LOT-1 đã release, còn nhiều hàng.</summary>
    private sealed class Lots : IMaterialLotStore
    {
        private Nvm.Material.Entities.MaterialLot _lot = new("LOT-1", "ANODE", 1000m, "m", At.AddDays(-1), null, null, null,
            Nvm.Material.Entities.LotQuality.Released, 1);

        public Task<Nvm.Material.Entities.MaterialLot?> LoadForUpdateAsync(string siteId, string lotId, CancellationToken cancellationToken) =>
            Task.FromResult<Nvm.Material.Entities.MaterialLot?>(lotId == _lot.LotId ? _lot : null);

        public Task CreateAsync(string siteId, Nvm.Material.Entities.MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpdateAsync(string siteId, Nvm.Material.Entities.MaterialLot lot, DateTimeOffset at, CancellationToken cancellationToken)
        {
            _lot = lot;
            return Task.CompletedTask;
        }

        public Task<Nvm.Material.Entities.MaterialLot?> OlderAvailableAsync(string siteId, Nvm.Material.Entities.MaterialLot lot,
            DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<Nvm.Material.Entities.MaterialLot?>(null);

        public Task<IReadOnlyList<Nvm.Material.Entities.MaterialOverride>> OverridesAsync(string siteId, string lotId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Nvm.Material.Entities.MaterialOverride>>([]);

        public Task AddOverrideAsync(string siteId, Nvm.Material.Entities.MaterialOverride grant, string actorId, DateTimeOffset at,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Units : IUnitExecutionContextReader
    {
        public UnitExecutionContext? Context { get; set; }

        public Task<UnitExecutionContext?> ReadForCommandAsync(string serialNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Context);
    }
}
