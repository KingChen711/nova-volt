namespace Nvm.Ingestion;

internal static class HealthProbe
{
    private const string Argument = "--health-probe";

    internal static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Argument, StringComparison.Ordinal);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint))
        {
            await Console.Error.WriteLineAsync($"Usage: {Argument} <absolute-url>");
            return 2;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
