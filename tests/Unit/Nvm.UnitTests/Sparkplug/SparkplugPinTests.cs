using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Org.Eclipse.Tahu.Protobuf;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>ADR-026, enforced rather than written down.</summary>
/// <remarks>
/// Generating our own decoder from the specification buys accuracy only for as long as the copy of
/// the specification stays a copy. Nothing about a wrong field number looks wrong: protobuf reads an
/// unrecognised number as an unknown field and keeps going, so a decoder built from an edited
/// schema returns a payload with fewer metrics rather than an error. These are the assertions that
/// turn that silence into a red test.
/// </remarks>
public sealed class SparkplugPinTests
{
    /// <summary>Last line of the provenance header we prepended; everything after it is upstream.</summary>
    private const string VerbatimMarker = "// --- UPSTREAM VERBATIM BELOW: do not edit a single byte ---";

    /// <summary>SHA-256 of eclipse-tahu/tahu@46f25e7 sparkplug_b/sparkplug_b.proto, LF endings.</summary>
    private const string UpstreamDigest = "4432c5c483b7fb9732d0594c98a2e97dca5e517e39c5374a8b918d837f0b4a19";

    /// <summary>Google.Protobuf 3.35.1, which is protobuf tag v35.1 — the commit protoc 35.1 is built from.</summary>
    private const string PinnedRuntimeVersion = "3.35.1+35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03";

    [Fact]
    public void TheVendoredSchemaIsStillTheSpecification()
    {
        // Devices encode against the upstream file, not against ours. Editing our copy — to silence a
        // linter, to "tidy" a comment, to try something during a debugging session — makes this repo
        // the only party in the building with that opinion.
        Sha256OfVendoredBody().ShouldBe(
            UpstreamDigest,
            "src/Platform/Nvm.Sparkplug/proto/sparkplug_b.proto no longer matches upstream. If that is "
                + "deliberate, update the digest here and the header in the .proto together — see ADR-026.");
    }

    [Fact]
    public void TheMarkerTheDigestDependsOnIsStillInTheFile()
    {
        // The positive control for the test above. Delete the marker line and Sha256OfVendoredBody
        // would hash the whole file, including our header — a different digest, and the failure would
        // read as "upstream changed" when nothing upstream did.
        Encoding.UTF8.GetString(SparkplugFixture.ReadAllBytesOfProto())
            .ShouldContain(VerbatimMarker);
    }

    [Fact]
    public void TheProtobufRuntimeIsTheOneProtocWasBuiltFrom()
    {
        // Grpc.Tools 2.83.0 ships `libprotoc 35.1`; Google.Protobuf 3.35.1 is built from the same
        // commit, and says so in its informational version. Pinning both to one commit removes the
        // question of which direction of skew is safe. A routine "update all packages" moves the
        // runtime and leaves protoc where it was, which is the unsafe direction.
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
        // If Payload ever arrives from some other assembly, somebody has checked generated C# into
        // the repository or added a Sparkplug library, and the digest test above stops protecting
        // anything — it would be guarding a file nothing compiles.
        typeof(Payload).Assembly.GetName().Name.ShouldBe("Nvm.Sparkplug");
    }

    [Fact]
    public void TheFieldNumbersThatCarryAMeasurementAreWhereTheSpecificationPutsThem()
    {
        // Named here because these five are the ones a mistake would be expensive in, and because
        // reading them off the generated file is harder than reading them here. Metric.alias is the
        // one to watch: report-by-exception traffic carries nothing else to identify a value by.
        Payload.SeqFieldNumber.ShouldBe(3);
        Payload.Types.Metric.NameFieldNumber.ShouldBe(1);
        Payload.Types.Metric.AliasFieldNumber.ShouldBe(2);
        Payload.Types.Metric.TimestampFieldNumber.ShouldBe(3);
        Payload.Types.Metric.DatatypeFieldNumber.ShouldBe(4);
    }

    private static string Sha256OfVendoredBody()
    {
        // Normalised to LF before hashing. .gitattributes already forces LF for this file, but a
        // digest that also depends on somebody's checkout settings fails for a reason that has
        // nothing to do with what the test is about.
        var text = Encoding.UTF8
            .GetString(SparkplugFixture.ReadAllBytesOfProto())
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = text.IndexOf(VerbatimMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the verbatim marker is gone from the .proto");
        start = text.IndexOf('\n', start) + 1;

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text[start..])));
    }
}
