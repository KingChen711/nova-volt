namespace Nvm.UnitTests.Sparkplug;

/// <summary>Đọc các artefact Sparkplug đã được copy ra cạnh test assembly.</summary>
/// <remarks>
/// Cùng hình dạng với <c>GoldenFile</c> trong contract test, và vì cùng một lý do: một file bị thiếu
/// không được phép đọc như "bước copy đang hỏng". Ở đây điều đó quan trọng hơn bình thường, vì các
/// payload là dữ liệu nhị phân — một lần đọc rỗng hoặc bị cắt cụt sẽ tạo ra một <c>Payload</c> với
/// không metric nào cả, đó là một protobuf message hoàn toàn hợp lệ và sẽ âm thầm khiến một assertion
/// thất bại vì lý do sai.
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
