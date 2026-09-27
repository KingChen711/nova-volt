using System.Text;
using Nvm.ErpGateway;

namespace Nvm.IntegrationTests;

/// <summary>Parser B2MML không cần container: đọc đúng, từ chối có dòng/cột, không tải DTD/thực thể ngoài.</summary>
public sealed class B2mmlParserTests
{
    [Fact]
    public void ValidSchedule_KeepsErpValuesAsSent()
    {
        var result = Parse(ErpGatewayTests.Schedule("PS-1", "WO-1", "NV-P120-NMC ", "9812", "480.5", "kg"));
        result.IsValid.ShouldBeTrue(string.Join(" | ", result.Errors));
        var request = result.Schedule!.Requests.Single();
        request.ProductCode.ShouldBe("NV-P120-NMC ");   // chuẩn hoá là việc của master data, không của parser
        request.EarliestStart.ShouldBe(new DateTimeOffset(2026, 8, 25, 6, 0, 0, TimeSpan.FromHours(7)));
        request.Materials.Single().ShouldSatisfyAllConditions(
            m => m.ExternalMaterialId.ShouldBe("9812"), m => m.Quantity.ShouldBe(480.5m), m => m.UnitOfMeasure.ShouldBe("kg"));
    }

    [Theory]
    [InlineData("abc", "QuantityString")]
    [InlineData("-5", "QuantityString")]
    public void InvalidQuantity_IsRejectedWithLineAndColumn(string quantity, string expected)
    {
        var result = Parse(ErpGatewayTests.Schedule("PS-1", "WO-1", "NV-P120-NMC", "9812", quantity, "kg"));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.StartsWith("Dòng ", StringComparison.Ordinal) && e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingWorkOrderId_WrongNamespace_AndDuplicateOrders_AreRejected()
    {
        Parse(ErpGatewayTests.Schedule("PS-1", "WO-1", "P", "M", "1", "kg").Replace("<ID>WO-1</ID>", "", StringComparison.Ordinal))
            .IsValid.ShouldBeFalse();
        Parse(ErpGatewayTests.Schedule("PS-1", "WO-1", "P", "M", "1", "kg")
                .Replace("B2MML-V0600", "B2MML-V0500", StringComparison.Ordinal)).IsValid.ShouldBeFalse();
        var twice = ErpGatewayTests.Schedule("PS-1", "WO-1", "P", "M", "1", "kg");
        var request = twice[twice.IndexOf("<ProductionRequest>", StringComparison.Ordinal)..(twice.IndexOf("</ProductionRequest>",
            StringComparison.Ordinal) + "</ProductionRequest>".Length)];
        var duplicate = Parse(twice.Replace(request, request + request, StringComparison.Ordinal));
        duplicate.IsValid.ShouldBeFalse();
        duplicate.Errors.Single().ShouldContain("WO-1");
    }

    [Fact]
    public void DoctypeAndExternalEntities_AreRefused()
    {
        var xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE ProductionSchedule [ <!ENTITY secret SYSTEM "file:///c:/windows/win.ini"> ]>
            <ProductionSchedule xmlns="http://www.mesa.org/xml/B2MML-V0600"><ID>&secret;</ID></ProductionSchedule>
            """;
        var result = Parse(xxe);
        result.IsValid.ShouldBeFalse();
        result.Errors.Single().ShouldContain("XML không đọc được");
    }

    private static B2mmlParseResult Parse(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return B2mmlParser.Parse(stream);
    }
}
