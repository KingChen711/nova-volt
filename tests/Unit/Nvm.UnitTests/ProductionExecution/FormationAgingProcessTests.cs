using Nvm.ProductionExecution.Entities;

namespace Nvm.UnitTests.ProductionExecution;

public sealed class FormationAgingProcessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    private static FormationAgingProcess In(FormationAgingState state) =>
        FormationAgingProcess.Start("NV1", "NV1CL16244A01001", "TRAY-1", 3, "NOVAVOLT/NV1/FORMATION/F1/FORM-01", T0)
            with
        { State = state };

    [Fact]
    public void Start_FormsWithAThirtySixHourDeadline()
    {
        var process = FormationAgingProcess.Start("NV1", "NV1CL16244A01001", "TRAY-1", 3, "NOVAVOLT/NV1/FORMATION/F1/FORM-01", T0);
        process.State.ShouldBe(FormationAgingState.Forming);
        process.Version.ShouldBe(0);
        process.FormationDueAt.ShouldBe(T0.AddHours(36));
        (process.SiteId, process.SerialNumber, process.TrayId, process.Channel).ShouldBe(("NV1", "NV1CL16244A01001", "TRAY-1", 3));
        process.EquipmentPath.ShouldBe("NOVAVOLT/NV1/FORMATION/F1/FORM-01");
        FormationAgingRules.AgingPeriod.ShouldBe(TimeSpan.FromDays(10));
        FormationAgingRules.DriftLimitMillivolt.ShouldBe(15m);
    }

    [Theory]
    [InlineData(FormationAgingState.Forming, null, "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE")]
    [InlineData(FormationAgingState.Degassing, "INVALID_PROCESS_STATE", null, "INVALID_PROCESS_STATE")]
    [InlineData(FormationAgingState.Aging, "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE", "AGING_NOT_ELAPSED")]
    [InlineData(FormationAgingState.AwaitingMeasurement, "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE", null)]
    [InlineData(FormationAgingState.Completed, "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE")]
    [InlineData(FormationAgingState.Faulted, "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE", "INVALID_PROCESS_STATE")]
    public void Transitions_AreOnlyAllowedFromTheirSourceState(FormationAgingState state, string? complete, string? aging,
        string? ocv2)
    {
        var process = In(state);
        process.CanCompleteFormation().ShouldBe(complete);
        process.CanStartAging().ShouldBe(aging);
        process.CanRecordOcv2().ShouldBe(ocv2);
    }

    [Theory]
    [InlineData(FormationAgingState.Forming, "FormationTimeout", true)]
    [InlineData(FormationAgingState.Degassing, "FormationTimeout", false)]
    [InlineData(FormationAgingState.Aging, "AgingDue", true)]
    [InlineData(FormationAgingState.Forming, "AgingDue", false)]
    [InlineData(FormationAgingState.Aging, "FormationTimeout", false)]
    [InlineData(FormationAgingState.Aging, "Unknown", false)]
    public void Timeout_OnlyAppliesWhileWaitingForThatKind(FormationAgingState state, string kind, bool applies) =>
        In(state).TimeoutApplies(kind).ShouldBe(applies);

    [Theory]
    [InlineData(FormationAgingState.Forming, true)]
    [InlineData(FormationAgingState.Degassing, true)]
    [InlineData(FormationAgingState.Aging, true)]
    [InlineData(FormationAgingState.AwaitingMeasurement, true)]
    [InlineData(FormationAgingState.Completed, false)]
    [InlineData(FormationAgingState.Faulted, false)]
    [InlineData(FormationAgingState.Quarantined, false)]
    public void TerminalStates_AreNotActive(FormationAgingState state, bool active) =>
        FormationAgingRules.IsActive(state).ShouldBe(active);

    [Fact]
    public void Drift_IsTheAbsoluteDifference()
    {
        FormationAgingProcess.Drift(4150m, 4131m).ShouldBe(19m);
        FormationAgingProcess.Drift(4131m, 4150m).ShouldBe(19m);
    }
}
