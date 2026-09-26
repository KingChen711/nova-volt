using System.Collections.Immutable;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.ProductionExecution.Commands;

public static class RollReasonCodes
{
    public const string AlreadyCoated = "ROLL_ALREADY_COATED";
    public const string OverlappingSegments = "OVERLAPPING_SEGMENTS";
}

/// <summary>Ghi bản đồ đoạn của một cuộn điện cực vừa phủ xong (scope §6.4).</summary>
public sealed record RecordRollCoatedCommand(
    string Site, string Actor, string Submission, DateTimeOffset Time,
    string RollId, ImmutableArray<RollSegment> Segments)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RecordRollCoated";

    public override string CanonicalPayload => Canonical([RollId, .. Segments.IsDefault ? [] : Segments
        .SelectMany(s => new[] { s.WebSide, Number(s.FromMeter), Number(s.ToMeter), s.SlurryBatchId,
            s.FoilLotId, s.RecipeVersionId, s.EquipmentId })]);

    /// <summary>Hai đoạn cùng mặt không được giao nhau; khoảng là nửa mở [from, to).</summary>
    public static bool Overlaps(ImmutableArray<RollSegment> segments) => segments
        .GroupBy(segment => segment.WebSide, StringComparer.Ordinal)
        .Any(side =>
        {
            var ordered = side.OrderBy(segment => segment.FromMeter).ToArray();
            return ordered.Zip(ordered.Skip(1)).Any(pair => pair.Second.FromMeter < pair.First.ToMeter);
        });
}

public sealed class RecordRollCoatedValidator : ICommandValidator<RecordRollCoatedCommand>
{
    public IEnumerable<ValidationFailure> Validate(RecordRollCoatedCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.RollId) || command.RollId.Length > 100)
        { yield return new ValidationFailure(nameof(command.RollId), "RollId phải có 1–100 ký tự."); }
        if (command.Segments.IsDefaultOrEmpty || command.Segments.Length > 1000)
        { yield return new ValidationFailure(nameof(command.Segments), "Cuộn phải có 1–1000 đoạn."); yield break; }
        foreach (var segment in command.Segments)
        {
            if (segment.WebSide is not ("A" or "B"))
            { yield return new ValidationFailure(nameof(command.Segments), "WebSide phải là A hoặc B."); }
            if (segment.FromMeter < 0 || segment.ToMeter <= segment.FromMeter)
            { yield return new ValidationFailure(nameof(command.Segments), "Mỗi đoạn phải có 0 ≤ from < to."); }
            if (new[] { segment.SlurryBatchId, segment.FoilLotId, segment.RecipeVersionId, segment.EquipmentId }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 100))
            { yield return new ValidationFailure(nameof(command.Segments), "Nguồn vật liệu của đoạn phải có 1–100 ký tự."); }
        }
    }
}
