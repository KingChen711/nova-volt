using System.Globalization;
using Nvm.CalendarLab;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.FactoryModel.Time;
using Nvm.Time;

const string HaiPhong = "NV1";
const string Leipzig = "DE1";

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

try
{
    var options = CalendarLabOptions.FromEnvironment();
    var seedDirectory = Path.Combine(AppContext.BaseDirectory, options.SeedDirectory);
    var (calendar, directory, revision) = BuildCalendar(seedDirectory);
    var haiPhongZone = RequireCalendar(directory, HaiPhong).TimeZone;
    var leipzigZone = RequireCalendar(directory, Leipzig).TimeZone;

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"NVM_CALENDAR_LAB_MODEL revision={revision} NV1_zone={haiPhongZone.Id} DE1_zone={leipzigZone.Id}"));

    var realFixture = await RealTelemetryFixtureReader.ReadAsync(
        options.ConnectionString,
        haiPhongZone,
        CancellationToken.None);
    var real = CalendarAssignmentAudit.Compare(realFixture.Assignments, calendar, HaiPhong);

    WriteResult(
        "real",
        HaiPhong,
        "c10_temperature_2026-07-20_2026-07-27",
        real,
        null);

    var ordinary = AuditShiftC(calendar, leipzigZone, ProductionDay.On(2026, 2, 14));
    var spring = AuditShiftC(calendar, leipzigZone, ProductionDay.On(2026, 3, 28));
    var autumn = AuditShiftC(calendar, leipzigZone, ProductionDay.On(2026, 10, 24));

    WriteResult("synthetic", Leipzig, "ordinary_shift_c", ordinary.Result, ordinary.Boundaries.Duration);
    WriteResult("synthetic", Leipzig, "spring_dst_shift_c", spring.Result, spring.Boundaries.Duration);
    WriteResult("synthetic", Leipzig, "autumn_dst_shift_c", autumn.Result, autumn.Boundaries.Duration);

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"NVM_CALENDAR_LAB_DST_DELTA spring_vs_ordinary_pp="
            + $"{spring.Result.MisassignedPercent - ordinary.Result.MisassignedPercent:F6} "
            + $"autumn_vs_ordinary_pp="
            + $"{autumn.Result.MisassignedPercent - ordinary.Result.MisassignedPercent:F6}"));

    RequireLabSensitivity(real, ordinary, spring, autumn);
    Console.WriteLine("NVM_CALENDAR_LAB_PASS real_fixture=1 synthetic_controls=3");

    return 0;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync($"Calendar lab failed: {failure.Message}");
    return 2;
}

static (ProductionCalendar Calendar, FactoryModelSiteCalendarDirectory Directory, int Revision) BuildCalendar(
    string seedDirectory)
{
    var catalog = FactoryModelSeed.LoadCatalog(seedDirectory);
    var model = catalog.Find(catalog.LatestRevision)
        ?? throw new InvalidOperationException($"'{seedDirectory}' has no factory-model revision.");
    var active = new InMemoryActiveFactoryModel();

    foreach (var site in model.Sites)
    {
        if (!active.TryActivate(new ActiveFactoryModelRevision(model.Revision, site), null))
        {
            throw new InvalidOperationException($"Could not activate revision {model.Revision} for {site.SiteId}.");
        }
    }

    var directory = new FactoryModelSiteCalendarDirectory(active);
    return (new ProductionCalendar(directory, TimeProvider.System), directory, model.Revision);
}

static SiteCalendar RequireCalendar(FactoryModelSiteCalendarDirectory directory, string siteId) =>
    directory.Find(siteId)
    ?? throw new InvalidOperationException($"Active factory model has no calendar for site '{siteId}'.");

static (CalendarAssignmentAuditResult Result, ShiftBoundaries Boundaries) AuditShiftC(
    IProductionCalendar calendar,
    TimeZoneInfo siteZone,
    ProductionDay day)
{
    var boundaries = calendar.GetShiftBoundaries(day, Shift.C, Leipzig);
    var rows = new List<CalendarAssignment>((int)boundaries.Duration.TotalMinutes);

    for (var instant = boundaries.Start; instant < boundaries.End; instant = instant.AddMinutes(1))
    {
        var naiveDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, siteZone).DateTime);
        rows.Add(new CalendarAssignment(instant, naiveDate));
    }

    return (CalendarAssignmentAudit.Compare(rows, calendar, Leipzig), boundaries);
}

static void WriteResult(
    string source,
    string siteId,
    string scenario,
    CalendarAssignmentAuditResult result,
    TimeSpan? elapsed)
{
    var elapsedText = elapsed is null
        ? "n/a"
        : elapsed.Value.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture);

    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"NVM_CALENDAR_LAB_RESULT source={source} site={siteId} scenario={scenario} "
            + $"rows={result.Rows} misassigned={result.MisassignedRows} "
            + $"percent={result.MisassignedPercent:F6} elapsed_minutes={elapsedText}"));
}

static void RequireLabSensitivity(
    CalendarAssignmentAuditResult real,
    (CalendarAssignmentAuditResult Result, ShiftBoundaries Boundaries) ordinary,
    (CalendarAssignmentAuditResult Result, ShiftBoundaries Boundaries) spring,
    (CalendarAssignmentAuditResult Result, ShiftBoundaries Boundaries) autumn)
{
    if (real.MisassignedRows == 0
        || ordinary.Result.MisassignedRows == 0
        || spring.Result.MisassignedRows == 0
        || autumn.Result.MisassignedRows == 0)
    {
        throw new InvalidOperationException("The destructive comparison did not expose the naive date assignment.");
    }

    if (ordinary.Boundaries.Duration != TimeSpan.FromHours(8)
        || spring.Boundaries.Duration != TimeSpan.FromHours(7)
        || autumn.Boundaries.Duration != TimeSpan.FromHours(9))
    {
        throw new InvalidOperationException("The DE1 control fixtures no longer span ordinary/spring/autumn shifts.");
    }

    if (spring.Result.MisassignedPercent == ordinary.Result.MisassignedPercent
        || autumn.Result.MisassignedPercent == ordinary.Result.MisassignedPercent)
    {
        throw new InvalidOperationException("The DST fixtures did not change the naive assignment error rate.");
    }
}
