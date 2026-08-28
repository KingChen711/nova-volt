namespace Nvm.UnitTests.Sparkplug;

/// <summary>Reads the Sparkplug artefacts that were copied next to the test assembly.</summary>
/// <remarks>
/// Same shape as <c>GoldenFile</c> in the contract tests, and for the same reason: a missing file
/// must not read as "the copy step is broken". Here it matters more than usual, because the payloads
/// are binary — an empty or truncated read produces a <c>Payload</c> with zero metrics, which is a
/// perfectly valid protobuf message and would quietly fail an assertion about the wrong thing.
/// </remarks>
internal static class SparkplugFixture
{
    private const string Directory = "sparkplug";

    internal const string DeviceBirth = "dbirth-form-01-ch-0142.bin";

    internal const string DeviceData = "ddata-form-01-ch-0142.bin";

    internal const string VendoredProto = "sparkplug_b.proto";

    internal static byte[] ReadBytes(string fileName) =>
        File.ReadAllBytes(Resolve(fileName));

    internal static byte[] ReadAllBytesOfProto() =>
        File.ReadAllBytes(Resolve(VendoredProto));

    private static string Resolve(string fileName)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, Directory, fileName);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Sparkplug fixture '{fileName}' is missing. Payloads live in tests/Fixtures/sparkplug/ "
                    + "and the vendored schema in src/Platform/Nvm.Sparkplug/proto/; both are copied by the csproj.",
                fullPath);
        }

        return fullPath;
    }
}
