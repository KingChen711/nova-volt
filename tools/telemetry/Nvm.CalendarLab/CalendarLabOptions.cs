namespace Nvm.CalendarLab;

/// <summary>Validated environment for the reproducible C14 calendar lab.</summary>
public sealed record CalendarLabOptions(string ConnectionString, string SeedDirectory)
{
    /// <summary>Reads the variables set by <c>make calendar-lab</c>.</summary>
    public static CalendarLabOptions FromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("NVM_CALENDAR_LAB_CONNECTION_STRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Environment variable NVM_CALENDAR_LAB_CONNECTION_STRING is required.");
        }

        var seedDirectory = Environment.GetEnvironmentVariable("NVM_CALENDAR_LAB_SEED_DIRECTORY");

        return new CalendarLabOptions(connectionString, seedDirectory ?? "seed");
    }
}
