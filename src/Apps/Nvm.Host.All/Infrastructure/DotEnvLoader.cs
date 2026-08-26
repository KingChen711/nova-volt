using System.Globalization;

namespace Nvm.Host.Infrastructure;

/// <summary>
/// Loads the repository root <c>.env</c> into the process environment during development.
/// </summary>
/// <remarks>
/// <para>
/// The same <c>.env</c> already drives docker-compose, so reading it here keeps one source of
/// truth for ports and credentials. The alternative — copying the six passwords into
/// <c>appsettings.Development.json</c> — means two committed files that must be kept in sync by
/// hand, and they will drift.
/// </para>
/// <para>
/// Development only. In any other environment the process environment is authoritative and this
/// loader does nothing, so a deployed app can never pick up a developer's file.
/// </para>
/// </remarks>
internal static class DotEnvLoader
{
    /// <summary>Loads <c>.env</c> from the nearest ancestor directory that contains one.</summary>
    /// <param name="contentRootPath">Directory to start searching upward from.</param>
    /// <returns>The file that was loaded, or <see langword="null"/> when none was found.</returns>
    public static string? Load(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                Apply(candidate);
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void Apply(string path)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // An existing variable always wins, so `NVM_X=... dotnet run` still overrides the file.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>Reads a required variable, failing loudly rather than producing a broken connection string.</summary>
    public static string Required(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Environment variable '{key}' is not set. Copy .env.example to .env, or set it in the environment."));
}
