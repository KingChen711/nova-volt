using System.Text.Json.Nodes;

namespace Nvm.ContractTests;

/// <summary>Đọc các golden file đã được copy ra cạnh test assembly.</summary>
internal static class GoldenFile
{
    private const string Directory = "golden";

    internal static string ReadText(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, Directory, relativePath);

        // Một golden file bị thiếu không được phép trông giống một test đang pass. Không có cái này,
        // việc đọc sẽ ném ra một FileNotFoundException với message nêu tên một đường dẫn dưới
        // artifacts/, và phỏng đoán đầu tiên luôn là "bước copy bị hỏng" thay vì "ai đó đã xóa contract".
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Golden file '{relativePath}' is missing. It should live in tests/Contract/golden/ and be copied by the csproj.",
                fullPath);
        }

        return File.ReadAllText(fullPath);
    }

    /// <summary>Đọc một golden file dưới dạng JSON tree, cho các so sánh bỏ qua thứ tự member.</summary>
    /// <remarks>
    /// Các member của JSON object vốn không có thứ tự theo định nghĩa, nên so sánh raw text sẽ biến
    /// một lần sắp xếp lại vô hại thành một test fail và dạy mọi người ghi lại file. So sánh tree chỉ
    /// fail khi hình dạng hoặc một giá trị thực sự thay đổi.
    /// </remarks>
    internal static JsonNode ReadNode(string relativePath) =>
        JsonNode.Parse(ReadText(relativePath))
        ?? throw new InvalidOperationException($"Golden file '{relativePath}' parsed to null.");
}
