namespace Nvm.CalendarLab;

/// <summary>Environment đã được validate cho calendar lab C14 có thể tái lập.</summary>
public sealed record CalendarLabOptions(string ConnectionString, string SeedDirectory)
{
    /// <summary>Đọc các biến được <c>make calendar-lab</c> thiết lập.</summary>
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
