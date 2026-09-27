using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Nvm.ProductionExecution.Commands;

namespace Nvm.ErpGateway;

/// <summary>Một ProductionRequest (work order) đọc từ B2MML, giữ nguyên mã ERP.</summary>
public sealed record ProductionRequestDocument(string WorkOrderId, string ProductCode, DateTimeOffset? EarliestStart,
    ImmutableArray<ErpMaterialRequirement> Materials);

public sealed record ProductionScheduleDocument(string ScheduleId, ImmutableArray<ProductionRequestDocument> Requests);

/// <summary>Kết quả đọc file: lịch hợp lệ, hoặc danh sách lỗi có dòng/cột để ghi vào <c>.error.txt</c>.</summary>
public sealed record B2mmlParseResult(ProductionScheduleDocument? Schedule, ImmutableArray<string> Errors)
{
    public bool IsValid => Schedule is not null && Errors.IsEmpty;
}

/// <summary>Đọc và kiểm schema B2MML ProductionSchedule. Không tải DTD hay tài nguyên ngoài (chặn XXE).</summary>
public static class B2mmlParser
{
    public const string Namespace = "http://www.mesa.org/xml/B2MML-V0600";
    private static readonly XNamespace B = Namespace;
    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);

    public static B2mmlParseResult Parse(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = ImmutableArray.CreateBuilder<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
            MaxCharactersInDocument = 20_000_000,
        };
        settings.ValidationEventHandler += (_, e) => errors.Add(string.Create(CultureInfo.InvariantCulture,
            $"Dòng {e.Exception.LineNumber}, cột {e.Exception.LinePosition}: {e.Message}"));
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(content, settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException error)
        {
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"XML không đọc được (dòng {error.LineNumber}, cột {error.LinePosition}): {error.Message}"));
            return new B2mmlParseResult(null, errors.ToImmutable());
        }
        if (document.Root?.Name != B + "ProductionSchedule")
        { errors.Add($"Phần tử gốc phải là ProductionSchedule trong namespace {Namespace}."); }
        if (errors.Count > 0)
        { return new B2mmlParseResult(null, errors.ToImmutable()); }
        var root = document.Root!;
        var requests = root.Elements(B + "ProductionRequest").Select(request => new ProductionRequestDocument(
            Value(request, "ID"),
            Value(request.Element(B + "ProductProductionRule")!, "ID"),
            request.Element(B + "EarliestStartTime") is { } start
                ? DateTimeOffset.Parse(start.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
            [.. request.Elements(B + "SegmentRequirement").Elements(B + "MaterialRequirement").Select(material =>
                new ErpMaterialRequirement(Value(material, "MaterialDefinitionID"),
                    decimal.Parse(material.Element(B + "Quantity")!.Element(B + "QuantityString")!.Value.Trim(),
                        NumberStyles.Number, CultureInfo.InvariantCulture),
                    material.Element(B + "Quantity")!.Element(B + "UnitOfMeasure")!.Value))])).ToImmutableArray();
        var duplicates = requests.GroupBy(r => r.WorkOrderId, StringComparer.Ordinal).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToArray();
        if (duplicates.Length > 0)
        {
            return new B2mmlParseResult(null,
                [$"Work order lặp trong cùng lịch: {string.Join(", ", duplicates)}."]);
        }
        return new B2mmlParseResult(new ProductionScheduleDocument(Value(root, "ID"), requests), []);
    }

    /// <summary>Giữ nguyên giá trị ERP gửi (kể cả khoảng trắng thừa); chuẩn hoá là việc của master data.</summary>
    private static string Value(XElement parent, string name) => parent.Element(B + name)!.Value;

    private static XmlSchemaSet LoadSchemas()
    {
        using var stream = typeof(B2mmlParser).Assembly.GetManifestResourceStream(
            "Nvm.ErpGateway.Schemas.b2mml-production-schedule-subset.xsd")
            ?? throw new InvalidOperationException("B2MML schema resource is missing.");
        var set = new XmlSchemaSet { XmlResolver = null };
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        set.Add(Namespace, reader);
        set.Compile();
        return set;
    }
}
