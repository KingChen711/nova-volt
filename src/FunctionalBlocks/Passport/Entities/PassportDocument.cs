using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nvm.Passport.Entities;

/// <summary>Model pin: dữ liệu dùng chung cho mọi pack cùng GTIN.</summary>
public sealed record BatteryModel(string Gtin, string ProductCode, string Manufacturer, string Chemistry, decimal NominalEnergyKwh,
    int ExpectedLifetimeCycles, bool RequiresPassport, string MaterialCompositionJson, string DismantlingUri, string SafetyUri);

/// <summary>Carbon footprint theo (sản phẩm, nhà máy, năm), có version.</summary>
public sealed record CarbonFootprint(string ProductCode, int Year, int Version, decimal KgCo2ePerKwh,
    ImmutableDictionary<string, decimal> RecycledContentPercent, string VerifiedBy);

/// <summary>Bằng chứng về một pack cụ thể, lấy từ genealogy và trạng thái unit lúc chuẩn bị passport.</summary>
public sealed record PackEvidence(string SerialNumber, string ProductCode, string QualityState,
    ImmutableArray<string> Genealogy, ImmutableArray<string> SupplierLots, ImmutableArray<string> RecipeVersions);

/// <summary>Một trường passport. Giá trị phức tạp là JSON text; nhóm truy cập lấy từ <see cref="PassportFieldCatalog"/>.</summary>
public sealed record PassportField(string Name, string Value);

/// <summary>Nội dung một version passport. Hash tính trên nội dung chuẩn hoá; đó là thứ compliance owner ký.</summary>
public sealed record PassportDocument(string SerialNumber, string Gtin, int Version, int? PreviousVersion,
    ImmutableArray<PassportField> Fields)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ContentSha256()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            SerialNumber,
            Gtin,
            Version,
            PreviousVersion,
            Fields = Fields.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new[] { f.Name, f.Value }),
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PassportDocument FromJson(string json) =>
        JsonSerializer.Deserialize<PassportDocument>(json, Json) ?? throw new InvalidDataException("Empty passport document.");
}

/// <summary>
/// Trường nào thuộc nhóm nào. Trường không có ở đây coi như không ai được xem: thêm trường mới mà quên gắn nhóm thì
/// passport vẫn an toàn (fail closed), chỉ là trường đó không hiện ra.
/// </summary>
public static class PassportFieldCatalog
{
    public static readonly FrozenDictionary<string, string> Classes = new Dictionary<string, string>
    {
        ["gtin"] = AccessClasses.Identity,
        ["serialNumber"] = AccessClasses.Identity,
        ["manufacturer"] = AccessClasses.Identity,
        ["manufacturingPlant"] = AccessClasses.Identity,
        ["manufactureDate"] = AccessClasses.Identity,
        ["chemistry"] = AccessClasses.Identity,
        ["nominalEnergyKwh"] = AccessClasses.Performance,
        ["expectedLifetimeCycles"] = AccessClasses.Performance,
        ["carbonFootprintKgCo2ePerKwh"] = AccessClasses.Sustainability,
        ["recycledContentPercent"] = AccessClasses.Sustainability,
        ["materialComposition"] = AccessClasses.Composition,
        ["dismantlingInstructionsUri"] = AccessClasses.Dismantling,
        ["safetyInstructionsUri"] = AccessClasses.Dismantling,
        ["stateOfHealthPercent"] = AccessClasses.Health,
        ["cycleCount"] = AccessClasses.Health,
        ["negativeEvents"] = AccessClasses.Health,
        ["genealogy"] = AccessClasses.Internal,
        ["supplierLots"] = AccessClasses.Internal,
        ["recipeVersions"] = AccessClasses.Internal,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string? ClassOf(string field) => Classes.GetValueOrDefault(field);
}

/// <summary>Passport đã lọc cho một bên đọc.</summary>
public sealed record PassportView(string SerialNumber, string Gtin, int Version, int? PreviousVersion, string Audience,
    IReadOnlyDictionary<string, string> Fields);

public static class PassportProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Dựng nội dung passport từ model, carbon record của nhà máy/năm và bằng chứng của pack.</summary>
    public static PassportDocument Build(string siteId, BatteryModel model, CarbonFootprint carbon, PackEvidence evidence,
        DateOnly manufactureDate, int version, int? previousVersion)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(carbon);
        ArgumentNullException.ThrowIfNull(evidence);
        // G29 bỏ số 0 thừa: 58.9000 đọc từ decimal(12,4) và 58.9 từ command cho cùng một chuỗi, cùng một hash.
        string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
        return new PassportDocument(evidence.SerialNumber, model.Gtin, version, previousVersion,
        [
            new("gtin", model.Gtin),
            new("serialNumber", evidence.SerialNumber),
            new("manufacturer", model.Manufacturer),
            new("manufacturingPlant", siteId),
            new("manufactureDate", manufactureDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new("chemistry", model.Chemistry),
            new("nominalEnergyKwh", Number(model.NominalEnergyKwh)),
            new("expectedLifetimeCycles", model.ExpectedLifetimeCycles.ToString(CultureInfo.InvariantCulture)),
            new("carbonFootprintKgCo2ePerKwh", Number(carbon.KgCo2ePerKwh)),
            new("recycledContentPercent", JsonSerializer.Serialize(
                carbon.RecycledContentPercent.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value), Json)),
            new("materialComposition", model.MaterialCompositionJson),
            new("dismantlingInstructionsUri", model.DismantlingUri),
            new("safetyInstructionsUri", model.SafetyUri),
            // Lúc xuất xưởng: SoH 100 %, chưa có chu kỳ; dữ liệu vận hành cập nhật bằng version sau.
            new("stateOfHealthPercent", "100"),
            new("cycleCount", "0"),
            new("negativeEvents", "[]"),
            new("genealogy", JsonSerializer.Serialize(evidence.Genealogy.Order(StringComparer.Ordinal), Json)),
            new("supplierLots", JsonSerializer.Serialize(evidence.SupplierLots.Order(StringComparer.Ordinal), Json)),
            new("recipeVersions", JsonSerializer.Serialize(evidence.RecipeVersions.Order(StringComparer.Ordinal), Json)),
        ]);
    }

    /// <summary>Chỉ trả trường mà <paramref name="audience"/> được xem; trường không có nhóm bị bỏ.</summary>
    public static PassportView View(PassportDocument document, string audience, bool isOwner)
    {
        ArgumentNullException.ThrowIfNull(document);
        var visible = document.Fields
            .Where(field => AccessPolicy.Allows(audience, PassportFieldCatalog.ClassOf(field.Name), isOwner))
            .ToImmutableSortedDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        return new PassportView(document.SerialNumber, document.Gtin, document.Version, document.PreviousVersion, audience, visible);
    }

    /// <summary>
    /// Ngày sản xuất từ serial (ADR-007): ký tự thứ 7 là chữ số cuối của năm, 8–10 là ngày trong năm. Chữ số năm lặp sau mười năm,
    /// nên chọn năm gần nhất không vượt quá <paramref name="reference"/>.
    /// </summary>
    public static DateOnly ManufactureDate(string serialNumber, DateOnly reference)
    {
        ArgumentNullException.ThrowIfNull(serialNumber);
        if (serialNumber.Length != 16 || !char.IsAsciiDigit(serialNumber[6])
            || !int.TryParse(serialNumber.AsSpan(7, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        { throw new ArgumentException("Serial không đúng định dạng 16 ký tự.", nameof(serialNumber)); }
        var year = reference.Year - reference.Year % 10 + (serialNumber[6] - '0');
        if (year > reference.Year)
        { year -= 10; }
        // Ngày 366 của năm không nhuận là serial sai, không phải ngày 1/1 năm sau.
        if (day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
        { throw new ArgumentException($"Ngày {day} không có trong năm {year}.", nameof(serialNumber)); }
        return new DateOnly(year, 1, 1).AddDays(day - 1);
    }
}
