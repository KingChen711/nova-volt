using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>ADR-026, được ép thực thi thay vì chỉ viết ra.</summary>
/// <remarks>
/// Tự generate decoder từ specification chỉ giữ được accuracy khi bản sao specification vẫn là bản
/// sao. Không có gì ở một field number sai trông có vẻ sai: protobuf đọc number không nhận ra thành
/// unknown field rồi tiếp tục, nên decoder build từ schema đã sửa trả về payload ít metric hơn thay vì
/// error. Đây là các assertion biến sự im lặng đó thành test đỏ.
/// </remarks>
public sealed class SparkplugPinTests
{
    /// <summary>Dòng cuối của provenance header ta thêm vào; mọi thứ sau nó là upstream.</summary>
    private const string VerbatimMarker = "// --- UPSTREAM VERBATIM BELOW: do not edit a single byte ---";

    /// <summary>SHA-256 của eclipse-tahu/tahu@46f25e7 sparkplug_b/sparkplug_b.proto, LF endings.</summary>
    private const string UpstreamDigest = "4432c5c483b7fb9732d0594c98a2e97dca5e517e39c5374a8b918d837f0b4a19";

    /// <summary>Google.Protobuf 3.35.1, tức protobuf tag v35.1 — commit mà protoc 35.1 được build từ.</summary>
    private const string PinnedRuntimeVersion = "3.35.1+35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03";

    [Fact]
    public void TheVendoredSchemaIsStillTheSpecification()
    {
        // Device encode theo file upstream, không theo file của ta. Sửa bản sao — để làm linter im,
        // "dọn" comment, hay thử gì đó trong debugging session — khiến repo này là bên duy nhất trong
        // tòa nhà có ý kiến đó.
        Sha256OfVendoredBody().ShouldBe(
            UpstreamDigest,
            "src/Platform/Nvm.Sparkplug/proto/sparkplug_b.proto no longer matches upstream. If that is "
                + "deliberate, update the digest here and the header in the .proto together — see ADR-026.");
    }

    [Fact]
    public void TheMarkerTheDigestDependsOnIsStillInTheFile()
    {
        // Positive control cho test trên. Xóa marker line thì Sha256OfVendoredBody sẽ hash cả file,
        // gồm cả header của ta — digest khác đi, và failure sẽ nói "upstream changed" khi upstream
        // không đổi gì.
        Encoding.UTF8.GetString(SparkplugFixture.ReadAllBytesOfProto())
            .ShouldContain(VerbatimMarker);
    }

    [Fact]
    public void TheProtobufRuntimeIsTheOneProtocWasBuiltFrom()
    {
        // Grpc.Tools 2.83.0 mang theo `libprotoc 35.1`; Google.Protobuf 3.35.1 build từ cùng commit
        // và nói vậy trong informational version. Pin cả hai về một commit loại bỏ câu hỏi skew theo
        // chiều nào là an toàn. Thao tác "update all packages" thông thường di chuyển runtime nhưng để
        // protoc tại chỗ, là chiều không an toàn.
        typeof(MessageParser).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .ShouldNotBeNull()
            .InformationalVersion
            .ShouldBe(
                PinnedRuntimeVersion,
                "Google.Protobuf moved away from the protoc that Grpc.Tools ships — read ADR-026 before bumping either");
    }

    [Fact]
    public void TheSparkplugTypesAreCompiledFromTheProtoAtBuildTime()
    {
        // Nếu Payload đến từ assembly khác, ai đó đã check generated C# vào repository hoặc thêm
        // Sparkplug library, và digest test ở trên không còn bảo vệ gì — nó sẽ canh một file không có
        // gì compile.
        typeof(Payload).Assembly.GetName().Name.ShouldBe("Nvm.Sparkplug");
    }

    [Fact]
    public void TheFieldNumbersThatCarryAMeasurementAreWhereTheSpecificationPutsThem()
    {
        // Đặt tên ở đây vì sai năm field này sẽ đắt giá, và vì đọc chúng từ generated file khó hơn đọc
        // ở đây. Metric.alias là field cần chú ý: traffic report-by-exception không mang gì khác để
        // định danh một value.
        Payload.SeqFieldNumber.ShouldBe(3);
        Payload.Types.Metric.NameFieldNumber.ShouldBe(1);
        Payload.Types.Metric.AliasFieldNumber.ShouldBe(2);
        Payload.Types.Metric.TimestampFieldNumber.ShouldBe(3);
        Payload.Types.Metric.DatatypeFieldNumber.ShouldBe(4);
    }

    private static string Sha256OfVendoredBody()
    {
        // Normalize về LF trước khi hash. .gitattributes đã ép LF cho file này, nhưng digest còn phụ
        // thuộc checkout settings của ai đó sẽ fail vì lý do không liên quan đến điều test này kiểm.
        var text = Encoding.UTF8
            .GetString(SparkplugFixture.ReadAllBytesOfProto())
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = text.IndexOf(VerbatimMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the verbatim marker is gone from the .proto");
        start = text.IndexOf('\n', start) + 1;

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text[start..])));
    }
}
