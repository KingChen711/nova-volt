using System.Globalization;
using Nvm.FactoryModel.Entities;
using Nvm.FactoryModel.Seeding;
using Nvm.Kernel.Identity;
using Nvm.LoadHarness;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var options = LoadHarnessOptions.FromEnvironment();
var linePath = EquipmentPath.Parse(options.LinePath);

// Plant nói ra channel nào tồn tại. Bịa ra danh sách này sẽ tạo ra các topic mà gateway từ chối
// (K3), và một harness mà mọi message đều bị từ chối chỉ đo được con đường từ chối ở 5.000 msg/s.
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

// Harness báo cáo; gate quyết định.
//
// Trước đây cái này còn yêu cầu thêm AchievedRate >= options.Rate, biến rate ĐƯỢC ĐỀ NGHỊ thành một
// mốc pass, một tầng dưới nơi load-gate.sh có cùng một lỗi. Một run ra kết quả thấp hơn đề nghị 4%
// sẽ exit 1 dù mọi message nó gửi đều đến đúng một lần - nên gate ở trên không bao giờ thấy được một
// run sạch, và "D2/M2 PASS" là một phán quyết không gì có thể chạm tới. Một rate có đủ tốt hay không
// là câu hỏi của N1 và nó được hỏi ở đúng một nơi, với ngưỡng đến từ scope.md thay vì từ bất kỳ điều
// gì process này được yêu cầu thử (ADR-031).
//
// Cái vẫn còn fail ở đây là cái chỉ harness mới thấy được: một publish bị lỗi, và một rebirth
// request, thứ nói rằng broker hoặc gateway đã lạc mất mạch của session chúng ta.
return result.Failed == 0 && result.RebirthRequests == 0 ? 0 : 1;

static List<EquipmentPath> ChannelsOf(FactoryModelSnapshot model, EquipmentPath linePath)
{
    // Mọi thứ model có dưới line này, đọc đúng theo cách simulator đọc nó. Phiên bản trước đó thay vào
    // đó đã đoán: nó hỏi xin từ FORM-01-CH-0001 đến FORM-01-CH-2000 và giữ lại cái nào trả lời, thứ
    // hoạt động trên một seed có một cycler và âm thầm chỉ tìm ra một phần mười của plant trên một
    // seed có mười cycler. deploy/seed-load/ đúng là hình dạng đó, nên load topology 1.000 channel lẽ
    // ra sẽ chạy như 100 channel và báo cáo một con số cho sai plant.
    //
    // Một harness hard-code một naming convention là một harness có quan điểm riêng về nhà máy. Nó
    // không được phép có quan điểm đó - factory model là nguồn của danh sách đó (K3).
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
