using System.Globalization;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.LoadHarness;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var options = LoadHarnessOptions.FromEnvironment();
var linePath = EquipmentPath.Parse(options.LinePath);

// The plant says which channels exist. Inventing the list would produce topics the gateway refuses
// (K3), and a harness whose every message is refused measures the rejection path at 5.000 msg/s.
var catalog = FactoryModelSeed.LoadCatalog(Path.Combine(AppContext.BaseDirectory, options.SeedDirectory));
var model = catalog.Find(catalog.LatestRevision)
    ?? throw new InvalidOperationException(
        $"'{options.SeedDirectory}' has no revision to read the channel list from.");

var channels = ChannelsOf(model, linePath);

Console.WriteLine(
    $"Load harness: {options.Rate} msg/s for {options.Duration} across {channels.Count} channels "
    + $"of {options.LinePath}, one MQTT session with at most {options.MaxInFlightPublishes} publishes "
    + $"in flight, broker {options.BrokerHost}:{options.BrokerPort}.");
Console.WriteLine("Clock drift is NOT injected: D2's lag subtracts two clocks, so a wrong one would be");
Console.WriteLine("measured as pipeline latency. Write that next to every number this produces.");

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

var result = await new LoadRunner(options, channels, TimeProvider.System).RunAsync(stopping.Token);
var collisions = result.Sent + result.DeclaredMessages - result.Measurements;

Console.WriteLine();
Console.WriteLine("  Ket qua harness (ve GUI)");
Console.WriteLine("  ------------------------------------------------------");
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Muc tieu                 : {options.Rate} msg/s"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Message gui thanh cong   : {result.Sent}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Publish loi              : {result.Failed}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Thoi gian chay           : {result.Elapsed}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Toc do dat duoc          : {result.AchievedRate:F0} msg/s"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Sequence gap / rebirth   : {result.RebirthRequests}"));
Console.WriteLine(
    string.Create(CultureInfo.InvariantCulture, $"  Phep do rieng biet       : {result.Measurements}"));
Console.WriteLine(
    string.Create(CultureInfo.InvariantCulture, $"  Trung mot mili-giay      : {collisions}"));
Console.WriteLine();
Console.WriteLine("  Sparkplug mang device timestamp bang MILI-GIAY, va khoa tu nhien cua mot phep do");
Console.WriteLine("  co device_timestamp trong do. Hai reading cung mot signal tren cung mot thiet bi");
Console.WriteLine("  trong cung mot mili-giay la MOT phep do, nen DB luu mot row — day khong phai mat.");
Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"NVM_LOAD_RESULT source_messages={result.Sent} "
        + $"source_measurements={result.Measurements} "
        + $"declared_messages={result.DeclaredMessages} collisions={collisions} "
        + $"failed={result.Failed} "
        + $"elapsed_seconds={result.Elapsed.TotalSeconds:F6} achieved_rate={result.AchievedRate:F6} "
        + $"rebirth_requests={result.RebirthRequests}"));
Console.WriteLine();
Console.WriteLine("  Ve NHAN (lag p50/p95/p99) doc o ingestion:");
Console.WriteLine("    curl -s http://ingestion:8080/api/ingestion/v1/stats");

// The harness reports; the gate decides.
//
// This used to also require AchievedRate >= options.Rate, which made the OFFERED rate a pass mark
// one level below where load-gate.sh had the same bug. A run that came out 4% under the offer
// exited 1 even when every message it did send arrived exactly once - so the gate above could never
// see a clean run, and "D2/M2 PASS" was a verdict nothing could reach. Whether a rate is good
// enough is N1's question and it is asked in one place, with the threshold that comes from
// scope.md rather than from whatever this process was asked to try (ADR-031).
//
// What still fails here is what only the harness can see: a publish that errored, and a rebirth
// request, which says the broker or the gateway lost the thread of our session.
return result.Failed == 0 && result.RebirthRequests == 0 ? 0 : 1;

static List<EquipmentPath> ChannelsOf(FactoryModelSnapshot model, EquipmentPath linePath)
{
    // Everything the model has under this line, read the way the simulator reads it. The previous
    // version guessed instead: it asked for FORM-01-CH-0001 through FORM-01-CH-2000 and kept what
    // answered, which works on a seed with one cycler and silently finds a tenth of the plant on a
    // seed with ten. deploy/seed-load/ is exactly that shape, so the 1.000-channel load topology
    // would have run as 100 channels and reported a number for the wrong plant.
    //
    // A harness that hard-codes a naming convention is a harness that has an opinion about the
    // factory. It is not allowed one - the factory model is the source of that list (K3).
    var prefix = linePath.Value + EquipmentPath.Separator;

    var channels = model.Paths
        .Where(path => path.Kind == FactoryNodeKind.Equipment
            && path.Value.StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(path => path.Value, StringComparer.Ordinal)
        .ToList();

    return channels.Count > 0
        ? channels
        : throw new InvalidOperationException(
            $"Revision {model.Revision} has no equipment under '{linePath.Value}', so there is "
            + "nothing to publish for. Check the seed the harness image was built with.");
}
