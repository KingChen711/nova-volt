using System.Text.Json.Nodes;

namespace Nvm.ContractTests;

/// <summary>Reads the golden files that were copied next to the test assembly.</summary>
internal static class GoldenFile
{
    private const string Directory = "golden";

    internal static string ReadText(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, Directory, relativePath);

        // A missing golden file must not look like a passing test. Without this the read would throw
        // a FileNotFoundException whose message names a path under artifacts/, and the first guess is
        // always "the copy step is broken" rather than "someone deleted the contract".
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Golden file '{relativePath}' is missing. It should live in tests/Contract/golden/ and be copied by the csproj.",
                fullPath);
        }

        return File.ReadAllText(fullPath);
    }

    /// <summary>Reads a golden file as a JSON tree, for comparisons that ignore member order.</summary>
    /// <remarks>
    /// JSON object members are unordered by definition, so comparing raw text would turn a harmless
    /// reordering into a failing test and train everyone to re-record the file. Comparing trees fails
    /// only when the shape or a value actually changed.
    /// </remarks>
    internal static JsonNode ReadNode(string relativePath) =>
        JsonNode.Parse(ReadText(relativePath))
        ?? throw new InvalidOperationException($"Golden file '{relativePath}' parsed to null.");
}
