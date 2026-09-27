using System.Collections.Immutable;
using System.Globalization;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Validation;

namespace Nvm.Passport.Commands;

public static class PassportReasonCodes
{
    public const string ModelExists = "BATTERY_MODEL_EXISTS";
    public const string ModelNotFound = "BATTERY_MODEL_NOT_FOUND";
    public const string PassportNotRequired = "PASSPORT_NOT_REQUIRED";
    public const string UnitNotFound = "UNIT_NOT_FOUND";
    public const string NotAPack = "NOT_A_PACK";
    public const string InvalidSerial = "INVALID_SERIAL";
    public const string UnitNotReleasable = "UNIT_NOT_RELEASABLE";
    public const string MissingEvidence = "MISSING_EVIDENCE";
    public const string DraftExists = "PASSPORT_DRAFT_EXISTS";
    public const string DraftNotFound = "PASSPORT_DRAFT_NOT_FOUND";
    public const string AlreadyPublished = "PASSPORT_ALREADY_PUBLISHED";
}

public sealed record DefineBatteryModelCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string Gtin, string ProductCode, string Manufacturer, string Chemistry, decimal NominalEnergyKwh, int ExpectedLifetimeCycles,
    bool RequiresPassport, string MaterialCompositionJson, string DismantlingUri, string SafetyUri)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "DefineBatteryModel";
    public override string CanonicalPayload => Canonical(Gtin, ProductCode, Manufacturer, Chemistry, Number(NominalEnergyKwh),
        ExpectedLifetimeCycles.ToString(CultureInfo.InvariantCulture), RequiresPassport ? "1" : "0", MaterialCompositionJson,
        DismantlingUri, SafetyUri);
}

/// <summary>Carbon footprint của sản phẩm tại nhà máy (site của command) cho một năm; ghi lại là version mới.</summary>
public sealed record RecordCarbonFootprintCommand(string Site, string Actor, string Submission, DateTimeOffset Time,
    string ProductCode, int Year, decimal KgCo2ePerKwh, ImmutableDictionary<string, decimal> RecycledContentPercent, string VerifiedBy)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "RecordCarbonFootprint";
    public override string CanonicalPayload => Canonical([ProductCode, Year.ToString(CultureInfo.InvariantCulture),
        Number(KgCo2ePerKwh), VerifiedBy,
        .. RecycledContentPercent.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={Number(p.Value)}")]);
}

/// <summary>Dựng bản nháp version kế tiếp của passport cho một pack từ bằng chứng hiện có.</summary>
public sealed record PreparePassportCommand(string Site, string Actor, string Submission, DateTimeOffset Time, string SerialNumber)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "PreparePassport";
    public override string CanonicalPayload => Canonical(SerialNumber);
}

/// <summary>Công bố bản nháp; cần chữ ký ComplianceOwner trên hash nội dung, không phải người dựng bản nháp.</summary>
public sealed record PublishPassportCommand(string Site, string Actor, string Submission, DateTimeOffset Time, string SerialNumber,
    int Version, ImmutableArray<string> SignatureIds)
    : DurableCommand(Site, Actor, Submission, Time)
{
    public override string CommandType => "PublishPassport";
    public override string CanonicalPayload => Canonical([SerialNumber, Version.ToString(CultureInfo.InvariantCulture),
        .. SignatureIds.Order(StringComparer.Ordinal)]);
}

public sealed class PassportCommandValidator : ICommandValidator<DefineBatteryModelCommand>,
    ICommandValidator<RecordCarbonFootprintCommand>, ICommandValidator<PreparePassportCommand>,
    ICommandValidator<PublishPassportCommand>
{
    public IEnumerable<ValidationFailure> Validate(DefineBatteryModelCommand command)
    {
        if (!Gtin14.IsValid(command.Gtin))
        { yield return new ValidationFailure(nameof(command.Gtin), "GTIN phải có 14 chữ số và đúng chữ số kiểm tra."); }
        foreach (var failure in Text(command.ProductCode, "ProductCode", 100).Concat(Text(command.Manufacturer, "Manufacturer", 200))
                     .Concat(Text(command.Chemistry, "Chemistry", 50)).Concat(Text(command.DismantlingUri, "DismantlingUri", 500))
                     .Concat(Text(command.SafetyUri, "SafetyUri", 500)).Concat(Text(command.MaterialCompositionJson, "MaterialComposition", 20_000)))
        { yield return failure; }
        if (command.NominalEnergyKwh <= 0 || command.ExpectedLifetimeCycles <= 0)
        { yield return new ValidationFailure(nameof(command.NominalEnergyKwh), "Năng lượng danh định và tuổi thọ phải dương."); }
    }

    public IEnumerable<ValidationFailure> Validate(RecordCarbonFootprintCommand command)
    {
        foreach (var failure in Text(command.ProductCode, "ProductCode", 100).Concat(Text(command.VerifiedBy, "VerifiedBy", 200)))
        { yield return failure; }
        if (command.Year is < 2020 or > 2100 || command.KgCo2ePerKwh <= 0)
        { yield return new ValidationFailure(nameof(command.Year), "Năm 2020–2100 và carbon footprint dương."); }
        if (command.RecycledContentPercent is null || command.RecycledContentPercent.Values.Any(v => v is < 0 or > 100))
        { yield return new ValidationFailure(nameof(command.RecycledContentPercent), "Tỉ lệ tái chế 0–100 %."); }
    }

    public IEnumerable<ValidationFailure> Validate(PreparePassportCommand command) =>
        command.SerialNumber is { Length: 16 } ? [] : [new ValidationFailure("SerialNumber", "Serial phải có 16 ký tự.")];

    public IEnumerable<ValidationFailure> Validate(PublishPassportCommand command) =>
        command.SerialNumber is { Length: 16 } && command.Version >= 1 && !command.SignatureIds.IsDefaultOrEmpty
            ? [] : [new ValidationFailure("SignatureIds", "Cần serial, version và chữ ký.")];

    private static IEnumerable<ValidationFailure> Text(string? value, string field, int maximum) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximum
            ? [new ValidationFailure(field, $"{field} phải có 1–{maximum} ký tự.")] : [];
}

public static class Gtin14
{
    /// <summary>GTIN-14: 14 chữ số, chữ số cuối là check digit GS1 (trọng số 3/1 từ phải sang).</summary>
    public static bool IsValid(string? gtin)
    {
        if (gtin is not { Length: 14 } || !gtin.All(char.IsAsciiDigit))
        { return false; }
        var sum = 0;
        for (var i = 0; i < 13; i++)
        { sum += (gtin[i] - '0') * (i % 2 == 0 ? 3 : 1); }
        return (10 - sum % 10) % 10 == gtin[13] - '0';
    }
}
