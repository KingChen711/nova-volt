using System.Text.Json;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Kernel.Commands;
using Nvm.Kernel.EventSourcing;
using Nvm.ProductionExecution.Commands;
using Nvm.ProductionExecution.Ports;

namespace Nvm.ProductionExecution.Handlers;

/// <summary>Kiểm điều kiện nghiệp vụ trên context SQL, rồi ghi kết quả đo — hoặc từ chối có lý do.</summary>
/// <remarks>
/// <para>
/// Chạy BÊN TRONG transaction do <c>IdempotencyBehavior</c> mở khi claim (ADR-023): đọc context và
/// INSERT dùng cùng scoped session, nên state được kiểm không đổi giữa lúc kiểm và lúc ghi. Handler
/// ghi event cùng transaction; outbox worker của host publish sau commit.
/// </para>
/// <para>
/// Command này ghi một fact nhập tay: <b>không</b> đổi execution/quality/location state, không phát
/// <c>ProcessStepCompleted</c>/<c>MeasurementRecorded</c>, không suy ra ngưỡng đạt chất lượng (ADR-038).
/// Business rejection lưu outcome nhưng KHÔNG INSERT và KHÔNG nạp event.
/// </para>
/// </remarks>
public sealed class RecordDataCollectionHandler(
    IProductionContextSource context,
    IDataCollectionStore store,
    IEventStore events,
    TimeProvider clock)
    : ICommandHandler<RecordDataCollectionCommand, RecordDataCollectionResult>
{
    private const string ExpectedStep = "EOL";
    private const string PackKind = "Pack";
    private const string RunningState = "Running";
    private const string HeldState = "Held";
    private const string ScrappedState = "Scrapped";

    private static readonly JsonSerializerOptions PayloadJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IProductionContextSource _context = context;
    private readonly IDataCollectionStore _store = store;
    private readonly IEventStore _events = events;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public async Task<RecordDataCollectionResult> HandleAsync(
        RecordDataCollectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var correlationId = command.IdempotencyKey.Value.ToString();
        var snapshot = await _context.FindAsync(command.SerialNumber, cancellationToken).ConfigureAwait(false);

        // Không tìm thấy trong site: thông điệp giống hệt cho serial ngoài site, không lộ tồn tại (K3).
        if (snapshot is null)
        {
            return Reject(DataCollectionReasonCodes.UnitNotFound,
                "Không tìm thấy serial trong site hiện tại.", [], correlationId);
        }

        if (Evaluate(command, snapshot, correlationId) is { } rejection)
        {
            return rejection;
        }

        var recordedAt = _clock.GetUtcNow();
        var payloadJson = JsonSerializer.Serialize(
            new RecordDataCollectionPayload(
                command.SubmissionId, command.SerialNumber, command.OperationRunId, command.StepCode,
                command.EquipmentPath, command.SignalCode, command.Value, command.UnitOfMeasure),
            PayloadJsonOptions);

        await _store.AppendAsync(
            new DataCollectionRecord(
                command.SiteId,
                command.IdempotencyKey.Value,
                command.SubmissionId,
                command.SerialNumber,
                command.OperationRunId,
                command.StepCode,
                command.EquipmentPath,
                command.SignalCode,
                command.ValueText,
                command.UnitOfMeasure,
                command.ActorId,
                command.OccurredAt,
                recordedAt,
                payloadJson),
            cancellationToken).ConfigureAwait(false);

        // Event and outbox intent share the collection/claim/outcome transaction.
        var recorded = new DataCollectionRecorded(
            EventId: command.IdempotencyKey.Value,
            OccurredAt: command.OccurredAt,
            RecordedAt: recordedAt,
            SiteId: command.SiteId,
            SubmissionId: command.SubmissionId,
            SerialNumber: command.SerialNumber,
            OperationRunId: command.OperationRunId,
            StepCode: command.StepCode,
            EquipmentPath: command.EquipmentPath,
            SignalCode: command.SignalCode,
            Value: command.Value,
            UnitOfMeasure: command.UnitOfMeasure,
            ActorId: command.ActorId);

        var eventType = EventTypeName.Of(typeof(DataCollectionRecorded));
        var metadata = JsonSerializer.Serialize(new
        {
            correlationid = correlationId,
            causationid = correlationId,
            subject = command.SerialNumber,
            partitionkey = command.SiteId + ":" + command.SerialNumber,
        });
        await _events.AppendAsync(command.SiteId, "data-collection:" + command.SubmissionId,
            "data-collection", 0,
            [new NewStreamEvent(recorded.EventId, eventType.Value, eventType.Version,
                JsonSerializer.Serialize(recorded, PayloadJsonOptions), metadata,
                recorded.OccurredAt, recorded.RecordedAt)], cancellationToken).ConfigureAwait(false);

        return new RecordDataCollectionResult(
            Accepted: true,
            ReasonCode: DataCollectionReasonCodes.Accepted,
            ReasonText: "Đã ghi nhận kết quả đo. Đây không phải xác nhận pack đã đạt chất lượng.",
            BlockingRules: [],
            AllowedNextActions: DataCollectionNextActions.Standard,
            CorrelationId: correlationId);
    }

    // Trả rejection đầu tiên gặp phải; ưu tiên lý do chất lượng (Held/Scrapped) trước "không Running",
    // để một pack bị scrap được báo đúng là SCRAPPED chứ không phải OPERATION_NOT_RUNNING.
    private static RecordDataCollectionResult? Evaluate(
        RecordDataCollectionCommand command, ProductionUnitSnapshot snapshot, string correlationId)
    {
        if (!string.Equals(snapshot.UnitKind, PackKind, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.NotAPack,
                "Unit không phải pack; form này chỉ nhập cho pack.",
                [new BlockingRule("UnitKind", PackKind, snapshot.UnitKind)], correlationId);
        }

        if (!string.Equals(snapshot.OperationRunId, command.OperationRunId, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.OperationRunMismatch,
                "Operation run không thuộc pack này.",
                [new BlockingRule("OperationRunId", snapshot.OperationRunId, command.OperationRunId)],
                correlationId);
        }

        if (!string.Equals(snapshot.StepCode, ExpectedStep, StringComparison.Ordinal)
            || !string.Equals(command.StepCode, snapshot.StepCode, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.StepNotEol,
                "Chỉ nhận kết quả đo tại công đoạn EOL.",
                [new BlockingRule("StepCode", ExpectedStep,
                    snapshot.StepCode == ExpectedStep ? command.StepCode : snapshot.StepCode)], correlationId);
        }

        if (!string.Equals(snapshot.EquipmentPath, command.EquipmentPath, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.EquipmentMismatch,
                "Trạm không khớp trạm được giao cho unit này.",
                [new BlockingRule("EquipmentPath", snapshot.EquipmentPath, command.EquipmentPath)],
                correlationId);
        }

        if (string.Equals(snapshot.QualityState, HeldState, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.QualityHold,
                "Pack đang bị giữ chất lượng; không nhận kết quả đo. Điều tra hàng hold thuộc luồng Quality.",
                [new BlockingRule("QualityState", "not " + HeldState, snapshot.QualityState)], correlationId);
        }

        if (string.Equals(snapshot.QualityState, ScrappedState, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.Scrapped,
                "Pack đã bị loại bỏ; không nhận thêm kết quả đo.",
                [new BlockingRule("QualityState", "not " + ScrappedState, snapshot.QualityState)], correlationId);
        }

        if (!string.Equals(snapshot.ExecutionState, RunningState, StringComparison.Ordinal))
        {
            return Reject(DataCollectionReasonCodes.OperationNotRunning,
                "Operation run chưa ở trạng thái Running; chưa thể nhập kết quả đo.",
                [new BlockingRule("ExecutionState", RunningState, snapshot.ExecutionState)], correlationId);
        }

        return null;
    }

    private static RecordDataCollectionResult Reject(
        string reasonCode, string reasonText, IReadOnlyList<BlockingRule> blockingRules, string correlationId) =>
        new(Accepted: false,
            ReasonCode: reasonCode,
            ReasonText: reasonText,
            BlockingRules: blockingRules,
            AllowedNextActions: DataCollectionNextActions.Standard,
            CorrelationId: correlationId);
}
