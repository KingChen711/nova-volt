using System.Globalization;
using System.Text;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.ProductionExecution.Commands;

/// <summary>Ghi nhận một kết quả đo nhập tay tại EOL; ý định có thể bị từ chối, không đổi state.</summary>
/// <remarks>
/// <para>
/// Là một <see cref="IDurableCommand"/>: claim + effect + outcome commit trong một SQL transaction
/// (ADR-023). <see cref="SiteId"/> và <see cref="ActorId"/> chỉ được đặt từ principal đã xác thực —
/// dựng command bằng <see cref="RecordDataCollectionCommandFactory"/>, không nhận từ dữ liệu client khai.
/// </para>
/// <para>
/// <c>RecordedAt</c> cố tình <b>không</b> nằm trên command: nó do server đặt trong handler bằng
/// <see cref="TimeProvider"/>, nên caller không giả tạo được thời điểm ghi nhận.
/// </para>
/// </remarks>
public sealed record RecordDataCollectionCommand : IDurableCommand, ICommand<RecordDataCollectionResult>
{
    /// <summary>Tên contract ổn định, không phụ thuộc tên class/namespace khi refactor.</summary>
    public const string ContractCommandType = "RecordDataCollection";

    /// <summary>Chỉ <see cref="RecordDataCollectionCommandFactory"/> dựng được, để site/actor luôn từ principal.</summary>
    internal RecordDataCollectionCommand(
        IdempotencyKey idempotencyKey,
        string siteId,
        string actorId,
        string submissionId,
        DateTimeOffset occurredAt,
        string serialNumber,
        string operationRunId,
        string stepCode,
        string equipmentPath,
        string signalCode,
        decimal value,
        string unitOfMeasure)
    {
        IdempotencyKey = idempotencyKey;
        SiteId = siteId;
        ActorId = actorId;
        SubmissionId = submissionId;
        OccurredAt = occurredAt;
        SerialNumber = serialNumber;
        OperationRunId = operationRunId;
        StepCode = stepCode;
        EquipmentPath = equipmentPath;
        SignalCode = signalCode;
        Value = value;
        UnitOfMeasure = unitOfMeasure;
    }

    /// <inheritdoc />
    public IdempotencyKey IdempotencyKey { get; }

    /// <inheritdoc />
    public string CommandType => ContractCommandType;

    /// <inheritdoc />
    public string SiteId { get; }

    /// <inheritdoc />
    public string ActorId { get; }

    /// <inheritdoc />
    public string SubmissionId { get; }

    /// <summary>Thời điểm người dùng xác nhận nhập; giữ nguyên khi retry.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Serial pack đã đo.</summary>
    public string SerialNumber { get; }

    /// <summary>Lần chạy công đoạn được nhập.</summary>
    public string OperationRunId { get; }

    /// <summary>Công đoạn — M4 là <c>EOL</c>.</summary>
    public string StepCode { get; }

    /// <summary>Trạm được giao.</summary>
    public string EquipmentPath { get; }

    /// <summary>Metric — M4 là <c>PackVoltage</c>.</summary>
    public string SignalCode { get; }

    /// <summary>Giá trị đo (decimal, giữ đủ chữ số).</summary>
    public decimal Value { get; }

    /// <summary>Đơn vị — M4 là <c>V</c>.</summary>
    public string UnitOfMeasure { get; }

    /// <summary>Giá trị đo dưới dạng text round-trip, để lưu SQL không ép fixed-scale.</summary>
    public string ValueText => Value.ToString("G29", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    /// <remarks>
    /// Tất định trên TOÀN BỘ nội dung nghiệp vụ đã đóng băng — gồm <see cref="OccurredAt"/> chuẩn hoá về
    /// UTC và giá trị số — nhưng bỏ metadata transport (actor, recordedAt, correlation) đúng theo
    /// ADR-038. Mã hoá có tiền tố độ dài để injective: hai payload khác nhau không thể cho cùng chuỗi.
    /// </remarks>
    public string CanonicalPayload
    {
        get
        {
            var builder = new StringBuilder();
            Append(builder, SubmissionId);
            Append(builder, SerialNumber);
            Append(builder, OperationRunId);
            Append(builder, StepCode);
            Append(builder, EquipmentPath);
            Append(builder, SignalCode);
            Append(builder, ValueText);
            Append(builder, UnitOfMeasure);
            Append(builder, OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }

    /// <summary>Suy ra khoá cho một submission tại một site, đúng cách mã hoá của kernel.</summary>
    public static IdempotencyKey KeyFor(string siteId, string submissionId) =>
        IdempotencyKey.FromNaturalKey(siteId, ContractCommandType, submissionId);

    private static void Append(StringBuilder builder, string part) =>
        builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part).Append('|');
}
