using System.Globalization;
using System.Text;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.Traceability.Commands;

public static class UnitReasonCodes
{
    public const string Accepted = "ACCEPTED";
    public const string DuplicateSerial = "DUPLICATE_SERIAL";
    public const string InvalidSerial = "INVALID_SERIAL";
    public const string SiteMismatch = "SITE_MISMATCH";
    public const string UnitNotFound = "UNIT_NOT_FOUND";
    public const string RoutingNotFound = "ROUTING_NOT_FOUND";
    public const string RoutingViolation = "ROUTING_VIOLATION";
    public const string StepAlreadyRunning = "STEP_ALREADY_RUNNING";
    public const string StepNotRunning = "STEP_NOT_RUNNING";
    public const string OperationRunMismatch = "OPERATION_RUN_MISMATCH";
    public const string EquipmentMismatch = "EQUIPMENT_MISMATCH";
    public const string QualityHold = "QUALITY_HOLD";
    public const string Scrapped = "SCRAPPED";
    public const string UnauthorizedTransition = "UNAUTHORIZED_TRANSITION";
    public const string InvalidInput = "INVALID_INPUT";
}

public sealed record UnitCommandResult(
    bool Accepted, string ReasonCode, Guid? EventId, long? StreamVersion, bool Quarantined = false);

public abstract record UnitCommand : IDurableCommand, ICommand<UnitCommandResult>
{
    protected UnitCommand(string siteId, string actorId, string submissionId, string serialNumber,
        DateTimeOffset occurredAt)
    {
        SiteId = siteId;
        ActorId = actorId;
        SubmissionId = submissionId;
        SerialNumber = serialNumber;
        OccurredAt = occurredAt;
        IdempotencyKey = IdempotencyKey.FromNaturalKey(siteId, CommandType, submissionId);
    }

    public IdempotencyKey IdempotencyKey { get; }
    public abstract string CommandType { get; }
    public string SiteId { get; }
    public string ActorId { get; }
    public string SubmissionId { get; }
    public string SerialNumber { get; }
    public DateTimeOffset OccurredAt { get; }
    public abstract string CanonicalPayload { get; }

    protected string Payload(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values.Prepend(SerialNumber).Append(OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
        {
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
        }

        return builder.ToString();
    }
}

public sealed record SerializeUnitCommand(
    string Site, string Actor, string Submission, string Serial,
    DateTimeOffset Time, string ProductCode, string WorkOrderId, string RoutingVersion)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "SerializeUnit";
    public override string CanonicalPayload => Payload(ProductCode, WorkOrderId, RoutingVersion);
}

public sealed record StartStepCommand(
    string Site, string Actor, string Submission, string Serial,
    DateTimeOffset Time, string StepCode, string OperationRunId, string EquipmentPath)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "StartStep";
    public override string CanonicalPayload => Payload(StepCode, OperationRunId, EquipmentPath);
}

public sealed record CompleteStepCommand(
    string Site, string Actor, string Submission, string Serial,
    DateTimeOffset Time, string StepCode, string OperationRunId)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "CompleteStep";
    public override string CanonicalPayload => Payload(StepCode, OperationRunId);
}

public sealed record RecordMeasurementCommand(
    string Site, string Actor, string Submission, string Serial,
    DateTimeOffset Time, string StepCode, string OperationRunId,
    string EquipmentPath, string SignalCode, decimal Value, string UnitOfMeasure)
    : UnitCommand(Site, Actor, Submission, Serial, Time)
{
    public override string CommandType => "RecordMeasurement";
    public override string CanonicalPayload => Payload(StepCode, OperationRunId, EquipmentPath,
        SignalCode, Value.ToString("G29", CultureInfo.InvariantCulture), UnitOfMeasure);
}
