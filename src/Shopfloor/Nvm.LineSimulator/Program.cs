using System.Globalization;
using Npgsql;
using Nvm.Hosting;
using Nvm.Shopfloor;
using Nvm.Shopfloor.LineDb;

// Dòng lệnh phía xưởng. Kết nối LineDB bằng owner (vai máy và lịch phía xưởng), lấy từ .env.
//   run    --line N [--cells 24] [--coils 2] [--ko-every 0] [--shrink 0] [--prefix P] [--start ISO]
//   relay  [--rows 5] [--interval 60]                     lịch gửi của đường relay (send_pending mỗi nhịp)
//   load   [--takt 5] [--minutes 60] [--lines 1,2,3,4] [--relay] [--rows 5] [--drain 10]
//   report --since ISO

DotEnvLoader.Load(AppContext.BaseDirectory);
DotEnvLoader.Load(Directory.GetCurrentDirectory());
var owner = new NpgsqlConnectionStringBuilder
{
    Host = "127.0.0.1",
    Port = int.Parse(DotEnvLoader.Required("NVM_PORT_LINEDB"), CultureInfo.InvariantCulture),
    Database = DotEnvLoader.Required("NVM_LINEDB_DB"),
    Username = DotEnvLoader.Required("NVM_LINEDB_USER"),
    Password = DotEnvLoader.Required("NVM_LINEDB_PASSWORD"),
}.ConnectionString;

// Máy thật chạy theo giờ thật (K1: qua TimeProvider, không đọc thẳng đồng hồ máy).
var clock = TimeProvider.System;
var verb = args.FirstOrDefault() ?? "help";
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

switch (verb)
{
    case "run":
        {
            var line = Int("--line", 1);
            var scenario = new LineScenario
            {
                Line = line,
                Start = Option("--start") is { } start ? DateTimeOffset.Parse(start, CultureInfo.InvariantCulture) : clock.GetUtcNow(),
                Prefix = Option("--prefix") ?? "R" + clock.GetUtcNow().ToString("MMddHHmm", CultureInfo.InvariantCulture),
                Coils = Int("--coils", 2),
                Cells = Int("--cells", 24),
                KoEvery = Int("--ko-every", 0),
                ShrinkLogs = Int("--shrink", 0),
            };
            var expected = new LineSimulator(owner).Run(scenario);
            Console.WriteLine($"Line {line}, prefix {scenario.Prefix}: {expected.UnitRows} unit rows ({expected.Coils} coils, {expected.Cells} cells, "
                + $"{expected.KoCells} KO, {expected.FormedCells} formed, {expected.Modules} modules), {expected.ShrinkRows} shrink rows.");
            Console.WriteLine($"MOM should get {expected.ProductMtus} product MTUs, {expected.Consumptions} consumptions, "
                + $"{expected.TemporaryMtus} Auto_ MTUs, {expected.LotMtus} lot MTUs.");
            break;
        }

    case "relay":
        {
            var rows = Int("--rows", 5);
            var interval = TimeSpan.FromSeconds(Int("--interval", 60));
            Console.WriteLine($"Relay schedule: send_pending({rows}) every {interval.TotalSeconds} s. Ctrl+C to stop.");
            while (!cancel.IsCancellationRequested)
            {
                var sent = LineRelay.SendPending(owner, rows, clock.GetUtcNow());
                Console.WriteLine($"{clock.GetUtcNow():HH:mm:ss} notified {sent} row(s)");
                await Delay(interval);
            }

            break;
        }

    case "load":
        {
            var takt = TimeSpan.FromSeconds(double.Parse(Option("--takt") ?? "5", CultureInfo.InvariantCulture));
            var minutes = Int("--minutes", 60);
            var lines = (Option("--lines") ?? "1,2,3,4").Split(',').Select(l => int.Parse(l, CultureInfo.InvariantCulture)).ToArray();
            var relay = args.Contains("--relay");
            var rows = Int("--rows", 5);
            var drain = TimeSpan.FromMinutes(Int("--drain", 10));
            var start = clock.GetUtcNow();
            var profile = new LineLoadProfile
            {
                Lines = lines,
                Takt = takt,
                Prefix = "LD" + start.ToString("MMddHHmm", CultureInfo.InvariantCulture),
            };
            var generator = new LineLoadGenerator(owner, profile, start);
            Console.WriteLine($"Load from {start:O}: lines {string.Join(',', lines)}, takt {takt.TotalSeconds} s "
                + $"(nominal {LineLoadProfile.NominalTakt.TotalSeconds} s), {minutes} min, path {(relay ? "relay" : "poll")}, prefix {profile.Prefix}.");
            Console.WriteLine("minute,rows,done,backlog,not_done,p50_s,p95_s,max_s");
            var end = start.AddMinutes(minutes);
            var nextRelay = start;
            var nextReport = start.AddMinutes(1);
            while (!cancel.IsCancellationRequested && clock.GetUtcNow() < end + drain)
            {
                var now = clock.GetUtcNow();
                if (now < end)
                {
                    generator.Tick(now);
                }

                if (relay && now >= nextRelay)
                {
                    LineRelay.SendPending(owner, rows, now);
                    nextRelay = now.AddSeconds(60);
                }

                if (now >= nextReport)
                {
                    Print((int)(now - start).TotalMinutes, LineDbReport.Snapshot(owner, start));
                    nextReport = nextReport.AddMinutes(1);
                }

                await Delay(TimeSpan.FromSeconds(1));
            }

            Console.WriteLine($"Generated {generator.RowsWritten} rows. Final:");
            Print((int)(clock.GetUtcNow() - start).TotalMinutes, LineDbReport.Snapshot(owner, start));
            break;
        }

    case "report":
        {
            var since = DateTimeOffset.Parse(Option("--since") ?? throw new ArgumentException("--since is required"), CultureInfo.InvariantCulture);
            Console.WriteLine("minute,rows,done,backlog,not_done,p50_s,p95_s,max_s");
            Print((int)(clock.GetUtcNow() - since).TotalMinutes, LineDbReport.Snapshot(owner, since));
            break;
        }

    default:
        Console.WriteLine("Usage: run | relay | load | report (see the comment at the top of Program.cs).");
        Environment.ExitCode = verb == "help" ? 0 : 1;
        break;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

int Int(string name, int fallback) => Option(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

async Task Delay(TimeSpan span)
{
    try
    {
        await Task.Delay(span, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C: vòng ngoài thoát.
    }
}

static void Print(int minute, LineDbSnapshot s) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{minute},{s.Rows},{s.Done},{s.Backlog},{s.Failed},{s.P50Seconds:0.0},{s.P95Seconds:0.0},{s.MaxSeconds:0.0}"));
