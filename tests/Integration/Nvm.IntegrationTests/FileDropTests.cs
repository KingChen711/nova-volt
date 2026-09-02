using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.FileDrop;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.RawCurves;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// C15: một end-of-line tester chưa từng nghe tới MQTT vẫn đi vào qua cùng một cánh cửa.
/// Hai adapter chỉ khác nhau ở cách chúng đọc dữ liệu và không khác gì khác — cùng natural key, cùng
/// claim, cùng transaction — vì hai định nghĩa dedup lệch nhau chỉ trong vài tháng, và triệu chứng là
/// một measurement bị lưu hai lần đúng vào những máy báo cáo qua cả hai route.
/// </summary>
public sealed class FileDropTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");
    private static readonly EquipmentPath OtherChannel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143");

    // Dùng interpolation thay vì string.Format: mỗi index một reading, và các analyzer nói đúng
    // rằng một format string dùng trong loop cần một CompositeFormat mà ở đây không cần tới.
    private static string Row(int index) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,2026-08-28T09:28:11.{index:000}Z,real,4.8{index:000}");

    [Fact]
    public async Task TheOriginalBytes_AreArchivedBeforeTheFileIsFiledAsProcessed()
    {
        // C12 từng có một WORM store mà không ai gọi tới: ArchiveAsync chỉ reachable được từ chính
        // test của nó, nên một nhà máy đang chạy vẫn giữ measurement nhưng lại vứt bỏ export gốc của
        // máy. Đây là dây nối.
        //
        // Điều được assert là hình dạng của claim mà một auditor sẽ kiểm tra: đúng từng byte trên
        // đĩa, máy nào tạo ra chúng, khoảng thời gian chúng bao phủ, và — K5 — ai đã bàn giao chúng
        // và vì sao. Digest và object lock thuộc về RawCurveArchiveTests; ở đây câu hỏi chỉ là có
        // bất kỳ ai gọi nó hay không.
        await using var harness = await FileDropHarness.StartAsync();

        var result = await harness.DropAsync("eol-export.csv", Lines(3));

        result.ShouldNotBeNull();
        var archived = harness.Archive.Calls.ShouldHaveSingleItem();
        archived.Descriptor.EquipmentPath.ShouldBe(Channel);
        archived.Descriptor.SiteId.ShouldBe("NV1");
        archived.Provenance.Actor.ShouldBe("ingestion:file-drop");
        archived.Provenance.Reason.ShouldContain("eol-export.csv");
        archived.Provenance.SupersedesArchiveId.ShouldBeNull();

        // Đúng từng byte những gì có trên đĩa, không phải một bản re-serialize từ các row đã parse.
        // Toàn bộ lý do archive tồn tại phụ thuộc vào việc đây chính là file mà máy đã ghi.
        archived.Bytes.ShouldBe(
            await File.ReadAllBytesAsync(
                Path.Combine(harness.ProcessedPath, "eol-export.csv"),
                TestContext.Current.CancellationToken));

        // Interval được đọc từ chính các sample, không phải từ tên file, và nó half-open giống mọi
        // interval khác ở đây: sample cuối cùng nằm trong nó đúng bằng một microsecond. Dùng tick sẽ
        // đọc dễ hiểu hơn nhưng không thể lưu được — archive giữ interval trong một `timestamptz`,
        // vốn chỉ giữ tới microsecond, nên một điểm end chỉ tồn tại ở độ phân giải 100 ns sẽ quay
        // lại thành một interval khác với interval đã ghi. Assertion này ghim chặt tick cho tới khi
        // một phép đo trên stack đang chạy cho thấy cái giá phải trả.
        var span = await harness.MeasuredSpanAsync();
        archived.Descriptor.CurveStartAt.ShouldBe(span.First);
        archived.Descriptor.CurveEndAt.ShouldBe(span.Last.AddTicks(TimeSpan.TicksPerMicrosecond));

        // Và file chỉ tới được `processed` sau khi bản gốc của nó đã được giữ lại.
        harness.Processed().ShouldHaveSingleItem().ShouldBe("eol-export.csv");
    }

    [Fact]
    public async Task AFileNamingTwoMachines_IsRejectedWholeAndStoresNothing()
    {
        // Một export là bản ghi của một máy cho một run. Một file trộn lẫn hai channel không có câu
        // trả lời duy nhất cho câu hỏi "curve này là của ai", nên nó không bao giờ có thể có bản gốc
        // — và một measurement không thể tạo ra bản gốc thì không phải là bằng chứng (K4).
        //
        // Phiên bản đầu tiên của fix này đã lưu các row, log một warning rồi xếp file vào
        // `processed`. Điều đó mâu thuẫn với guarantee đã nêu ba dòng phía trên trong cùng method,
        // và đó chính xác là failure mode mà adapter này tồn tại để ngăn chặn: một path trông như đã
        // được xử lý. Reject là một quyết định trên toàn bộ file, nên nó xảy ra trước khi row đầu
        // tiên được lưu.
        await using var harness = await FileDropHarness.StartAsync();

        var mixed = new List<string>(Lines(2))
        {
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143,,Formation/CapacityResult,"
            + "2026-08-28T09:28:12.000Z,real,4.900",
        };

        var result = await harness.DropAsync("mixed-export.csv", mixed);

        result.ShouldBeNull();
        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();

        var rejected = harness.Rejected();
        rejected.ShouldContain("mixed-export.csv");
        rejected.ShouldContain("mixed-export.csv.error");

        var reason = await harness.ReadRejectedAsync("mixed-export.csv.error");
        reason.ShouldContain("names 2 machines");
        reason.ShouldContain("Nothing was stored");

        // Tách thành hai export, cùng ba reading đó vẫn đi vào. Việc reject là về hình dạng của
        // file, không phải vì dữ liệu không dùng được — nếu không thì đây chỉ là mất dữ liệu kèm
        // theo một lời giải thích.
        (await harness.DropAsync("ch-0142.csv", Lines(2))).ShouldNotBeNull();
        (await harness.DropAsync("ch-0143.csv", [mixed[^1]])).ShouldNotBeNull();
        (await harness.CountTelemetryAsync()).ShouldBe(3);
        harness.Archive.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AFileWhoseSecondMachineOnlyAppearsOnAnUnparseableLine_IsStillRejectedWhole()
    {
        // Lỗ hổng mà fix trước để lại, được phát hiện bởi một audit độc lập vào ngày 2026-09-01.
        //
        // Trước đây, single-machine check đọc danh sách máy từ các measurement đã parse thành công.
        // Một dòng có thể fail ở VALUE mà vẫn nêu tên máy của nó hoàn toàn rõ ràng, nên một file
        // chứa các row tốt cho CH-0142 và một row hỏng cho CH-0143 đã nêu tên hai máy nhưng trông
        // như chỉ một: các row của CH-0142 được lưu, cả file được archive như bản gốc của CH-0142,
        // và nó được xếp vào `processed`. Một auditor lấy bản gốc đó sẽ nhận về những byte chứa cả
        // reading của một máy khác.
        await using var harness = await FileDropHarness.StartAsync();

        var mixed = new List<string>(Lines(2))
        {
            // Path hợp lệ, timestamp hợp lệ, value không phải là một số.
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143,,Formation/CapacityResult,"
            + "2026-08-28T09:28:12.000Z,real,not-a-number",
        };

        (await harness.DropAsync("value-broken-second-machine.csv", mixed)).ShouldBeNull();

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();

        var reason = await harness.ReadRejectedAsync("value-broken-second-machine.csv.error");
        reason.ShouldContain("names 2 machines");

        // Lời reject nêu tên cả hai, kể cả máy chỉ từng xuất hiện trên một dòng lỗi — nếu không một
        // operator đọc lỗi sẽ không biết phải tách file nào.
        reason.ShouldContain("FORM-01-CH-0142");
        reason.ShouldContain("FORM-01-CH-0143");
    }

    [Fact]
    public async Task AFileWhoseEveryDataLineFails_IsFiledUnderRejectedRatherThanProcessed()
    {
        // Một máy, không mập mờ, và không một reading nào đọc được. Không gì được lưu, nên không gì
        // thiếu bản gốc và K4 không bị vi phạm — nhưng trước đây file lại rơi vào `processed`, cái
        // tên vốn nói rằng các reading trong nó đã đi vào hệ thống. Một operator quét thư mục đó
        // không thể phân biệt trường hợp này với một export đã thành công, còn lời giải thích thì
        // nằm ở một thư mục khác.
        await using var harness = await FileDropHarness.StartAsync();

        var unreadable = new List<string>
        {
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,"
            + "2026-08-28T09:28:11.000Z,real,not-a-number",
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,"
            + "2026-08-28T09:28:12.000Z,real,also-not-a-number",
        };

        (await harness.DropAsync("all-lines-bad.csv", unreadable)).ShouldBeNull();

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();

        var rejected = harness.Rejected();
        rejected.ShouldContain("all-lines-bad.csv");
        rejected.ShouldContain("all-lines-bad.csv.error");

        var reason = await harness.ReadRejectedAsync("all-lines-bad.csv.error");
        reason.ShouldContain("not one of them could be read");
        reason.ShouldContain("rejected rather than processed");
    }

    [Fact]
    public async Task AHeaderOnlyFile_IsRejectedBecauseAnEmptyExportCanHideAnExporterFailure()
    {
        // Một export rỗng là CSV hợp lệ nhưng không phải một kết quả sản xuất thành công. Không có
        // máy nào để gán bản gốc bất biến vào, và đánh dấu nó là processed sẽ che giấu lỗi thường
        // gặp: một exporter ghi header rồi mất luôn run mà nó lẽ ra phải export.
        await using var harness = await FileDropHarness.StartAsync();

        (await harness.DropAsync("empty-export.csv", [])).ShouldBeNull();

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();
        harness.Rejected().ShouldContain("empty-export.csv");
        harness.Rejected().ShouldContain("empty-export.csv.error");

        var reason = await harness.ReadRejectedAsync("empty-export.csv.error");
        reason.ShouldContain("no data lines");
        reason.ShouldContain("exporter lost or skipped the run");
    }

    [Fact]
    public async Task AFileWithALineWhoseMachineCannotBeRead_IsRejectedWholeRatherThanAttributed()
    {
        // Nghiêm ngặt hơn cả "một dòng hỏng không làm reject cả file", và đây là chủ ý. Một VALUE
        // hỏng chỉ mất một measurement. Một IDENTITY hỏng làm mất khả năng nói file này là của ai,
        // và một archive được xếp dưới tên một máy có thể không sở hữu mọi dòng trong đó thì không
        // phải là bằng chứng.
        var logger = new RecordingLogger<FileDropProcessor>();
        await using var harness = await FileDropHarness.StartAsync(logger: logger);

        var unreadable = new List<string>(Lines(2))
        {
            "not-an-equipment-path,,Formation/CapacityResult,2026-08-28T09:28:12.000Z,real,4.900",
        };

        (await harness.DropAsync("identity-unreadable.csv", unreadable)).ShouldBeNull();

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();

        (await harness.ReadRejectedAsync("identity-unreadable.csv.error"))
            .ShouldContain("could not be read");

        logger.Entries.ShouldContain(entry =>
            entry.EventId.Id == 2511
            && entry.Message.Contains("unreadable equipment identity", StringComparison.Ordinal));
        logger.Entries.ShouldNotContain(entry => entry.EventId.Id == 2508);
    }

    [Fact]
    public async Task ReplacingThePublicPathAfterClaim_DoesNotChangeTelemetryArchiveOrProcessedBytes()
    {
        // Implementation cũ parse A, giải phóng file, ghi các row, rồi mới mở lại public path để
        // archive. Một exporter thay thế path đó trong lúc ingestion bị chặn khiến database mô tả A
        // trong khi object WORM và `processed` đều khẳng định B mới là bản gốc.
        await using var harness = await FileDropHarness.StartAsync(blockFileIngestion: true);

        var path = await harness.WriteDropAsync("replaced-during-ingest.csv", Lines(2));
        var snapshotA = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var processing = harness.ProcessPathAsync(path, TestContext.Current.CancellationToken);

        var blocker = harness.BlockingIngestor.ShouldNotBeNull();
        await blocker.Entered.WaitAsync(TestContext.Current.CancellationToken);

        var replacementB = new[]
        {
            CsvMeasurementReader.Header,
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,"
                + "2026-08-28T10:00:00.000Z,real,9.999",
        };
        var publishedB = await harness.PublishAsync(
            harness.DataPath("replaced-during-ingest.csv"),
            replacementB);
        var bytesB = await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken);

        blocker.Release();
        var result = await processing;
        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(2);
        result.Duplicates.ShouldBe(0);

        (await harness.CountTelemetryAsync()).ShouldBe(2);
        (await harness.MeasuredSpanAsync()).ShouldBe(
            (new DateTimeOffset(2026, 8, 28, 9, 28, 11, 1, TimeSpan.Zero),
             new DateTimeOffset(2026, 8, 28, 9, 28, 11, 2, TimeSpan.Zero)));

        harness.Archive.Calls.ShouldHaveSingleItem().Bytes.ShouldBe(snapshotA);
        (await File.ReadAllBytesAsync(
            Path.Combine(harness.ProcessedPath, "replaced-during-ingest.csv"),
            TestContext.Current.CancellationToken)).ShouldBe(snapshotA);

        // B thực sự là một drop mới và vẫn còn đó cho lần poll tiếp theo; hoàn tất A không được ghi
        // đè lên nó. B cũng phải vẫn còn ở trạng thái *published*: việc claim A đã mang theo
        // readiness của A trong một lần rename duy nhất, nên không có gì của B để claim của A tiêu
        // thụ mất.
        (await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken)).ShouldBe(bytesB);
        harness.InboxNames().ShouldHaveSingleItem().ShouldBe("replaced-during-ingest.csv.ready");
    }

    [Fact]
    public async Task AnExportNotYetPublished_IsNotClaimedAndNothingInItIsStored()
    {
        // Rename một file ra khỏi inbox không đóng handle mà exporter của nó vẫn đang giữ. Trên NFS,
        // exporter sau đó vẫn tiếp tục append vào cùng inode, nên ingestion lưu phần đầu của một run
        // còn phần đuôi thì bị xóa cùng với claim, mà không có gì ở đâu nói điều đó. Một mtime lặng
        // lẽ không thể phân biệt trường hợp đó với một file đã hoàn tất; chỉ có producer mới biết,
        // và việc rename sang tên published chính là cách nó nói ra.
        await using var harness = await FileDropHarness.StartAsync();

        var dataPath = harness.DataPath("still-being-written.csv");
        await File.WriteAllLinesAsync(
            dataPath,
            [CsvMeasurementReader.Header, .. Lines(2)],
            TestContext.Current.CancellationToken);

        var refusal = await Should.ThrowAsync<FileDropNotPublishedException>(() =>
            harness.ProcessPathAsync(dataPath, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("still-being-written.csv");
        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();
        harness.Rejected().ShouldBeEmpty();

        // Được để nguyên đúng chỗ cũ, không bị claim dở dang: export vẫn thuộc về exporter cho tới
        // khi rename nói khác đi.
        File.Exists(dataPath).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();

        var published = harness.PublishedPathFor(dataPath);
        File.Move(dataPath, published);
        var result = await harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(2);
        harness.Processed().ShouldHaveSingleItem().ShouldBe("still-being-written.csv");
    }

    [Fact]
    public async Task ClaimingAnExport_TakesItsReadinessWithItAndLeavesNothingInTheInbox()
    {
        // Đây là invariant đứng sau một lần rename duy nhất. Một phiên bản trước đó publish
        // readiness trong một file thứ hai bên cạnh data và xử lý cả hai theo tuần tự; mọi phần còn
        // sót lại của cặp đó là một flag trong inbox mà không có gì đứng sau nó, và export tiếp theo
        // tới dưới cùng cái tên đó sẽ thừa hưởng nó — đọc được ngay khi vừa xuất hiện, bất kể đã ghi
        // được bao nhiêu.
        await using var harness = await FileDropHarness.StartAsync();

        var published = await harness.WriteDropAsync("one-step.csv", Lines(2));
        harness.InboxNames().ShouldHaveSingleItem().ShouldBe("one-step.csv.ready");

        (await harness.ProcessPathAsync(published, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()
            .Inserted.ShouldBe(2);

        harness.InboxNames().ShouldBeEmpty();
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();
        harness.Processed().ShouldHaveSingleItem().ShouldBe("one-step.csv");
    }

    [Fact]
    public async Task RestoringAClaimedExport_TakesANameNoProducerWouldPublish()
    {
        // Trước đây, restore nhắm vào chính cái tên mà export đã tới dưới đó, đầu tiên bằng
        // File.Exists rồi sau đó bằng một exclusive create. Cả hai đều không chịu nổi một producer:
        // một POSIX rename sẽ thay thế bất cứ thứ gì đang ở đích, nên `mv` đi thẳng xuyên qua một
        // placeholder, rồi restore lại xóa mất export vừa đi vào — một file bị chính component có
        // nhiệm vụ duy nhất là không làm mất gì phá hủy. Không có gì ở phía này có thể bảo vệ một
        // cái tên mà producer có thể dùng, nên restore không còn nhắm vào một cái tên nào nữa.
        await using var harness = await FileDropHarness.StartAsync(blockFileIngestion: true);

        var published = await harness.WriteDropAsync("published-twice.csv", Lines(2));
        var snapshotA = await File.ReadAllBytesAsync(published, TestContext.Current.CancellationToken);
        var processing = harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        var blocker = harness.BlockingIngestor.ShouldNotBeNull();
        await blocker.Entered.WaitAsync(TestContext.Current.CancellationToken);

        var publishedB = await harness.PublishAsync(
            harness.DataPath("published-twice.csv"),
            [
                CsvMeasurementReader.Header,
                "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,"
                    + "2026-08-28T11:00:00.000Z,real,9.999",
            ]);
        var bytesB = await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken);

        blocker.ReleaseWith(new InvalidOperationException("the write transaction rolled back"));
        await Should.ThrowAsync<InvalidOperationException>(() => processing);

        (await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken)).ShouldBe(bytesB);

        // A quay lại ở trạng thái published, dưới một cái tên riêng của nó, để lần poll tiếp theo đọc
        // được cả hai và dedup quyết định cái nào là mới — thay vì một trong hai coi như chưa từng
        // tồn tại.
        var restored = harness.InboxNames()
            .Where(name => name.StartsWith("published-twice.retry-", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        restored.ShouldEndWith(".csv.ready");
        (await File.ReadAllBytesAsync(
            Path.Combine(harness.InboxPath, restored),
            TestContext.Current.CancellationToken)).ShouldBe(snapshotA);

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecoveringAClaimAbandonedByACrash_KeepsBothExports()
    {
        // Cùng một quy tắc trên startup path, nơi nó còn quan trọng hơn: process chết trong lúc đang
        // giữ một claim, và tới khi nó quay lại thì exporter đã publish lại cái tên đó.
        await using var harness = await FileDropHarness.StartAsync();

        var claimDirectory = Path.Combine(harness.ProcessingPath, "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(claimDirectory);
        var abandoned = Path.Combine(claimDirectory, "crashed-mid-claim.csv");
        await File.WriteAllLinesAsync(
            abandoned,
            [CsvMeasurementReader.Header, .. Lines(2)],
            TestContext.Current.CancellationToken);
        var snapshotA = await File.ReadAllBytesAsync(abandoned, TestContext.Current.CancellationToken);

        var publishedB = await harness.PublishAsync(
            harness.DataPath("crashed-mid-claim.csv"),
            [
                CsvMeasurementReader.Header,
                "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,"
                    + "2026-08-28T11:30:00.000Z,real,7.777",
            ]);
        var bytesB = await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken);

        harness.RestartWatcher();

        (await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken)).ShouldBe(bytesB);

        var recovered = harness.InboxNames()
            .Where(name => name.StartsWith("crashed-mid-claim.recovered-", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        var recoveredPath = Path.Combine(harness.InboxPath, recovered);

        recovered.ShouldEndWith(".csv.ready");
        (await File.ReadAllBytesAsync(recoveredPath, TestContext.Current.CancellationToken)).ShouldBe(snapshotA);
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();

        // Recovered nghĩa là đọc được: một file được trả lại ở trạng thái chưa published sẽ nằm ì ở
        // đó mãi mãi.
        (await harness.ProcessPathAsync(recoveredPath, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()
            .Inserted.ShouldBe(2);
    }

    [Fact]
    public async Task AnExportOfOneReading_ReachesProcessedLikeAnyOther()
    {
        // Export bình thường nhất mà một end-of-line tester ghi ra, và cũng là loại từng hoàn toàn
        // không thể archive được. `Describe` kết thúc interval một tick 100 ns sau reading duy nhất;
        // còn archive lưu nó trong một `timestamptz`, vốn chỉ giữ tới microsecond, nên interval tới
        // PostgreSQL với duration bằng 0 và row bị từ chối. File sau đó cứ vòng quanh mãi trong retry
        // loop. Mọi test ở đây đều dùng nhiều reading, nên không test nào phát hiện ra.
        await using var harness = await FileDropHarness.StartAsync();

        var published = await harness.WriteDropAsync("one-reading.csv", [Row(1)]);

        var result = await harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(1);
        harness.Processed().ShouldHaveSingleItem().ShouldBe("one-reading.csv");

        // Interval phải sống sót qua vòng round-trip lưu trữ ở độ phân giải microsecond, nên nó
        // không được mang theo bất kỳ phần nào nhỏ hơn một microsecond.
        var archived = harness.Archive.Calls.ShouldHaveSingleItem();
        archived.Descriptor.CurveEndAt.ShouldBeGreaterThan(archived.Descriptor.CurveStartAt);
        (archived.Descriptor.CurveEndAt.Ticks % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
        (archived.Descriptor.CurveStartAt.Ticks % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
    }

    [Fact]
    public async Task AnExportThatKeepsFailing_CountsItsRoundsInsteadOfGrowingItsName()
    {
        // Trước đây, retry marker được append vào bất kể cái tên nào mà export đã tới dưới đó, nên
        // mỗi vòng của loop làm cái tên dài thêm ~40 byte. Sau sáu vòng nó vượt quá giới hạn 255
        // byte, move thất bại, và export mắc kẹt dưới `.processing` chỉ với một dòng log để nói về
        // việc đó — failure mode ở đây là một file không thể xử lý được thì rốt cuộc cũng không thể
        // trả lại được.
        await using var harness = await FileDropHarness.StartAsync(blockFileIngestion: true);

        var published = await harness.WriteDropAsync("keeps-failing.csv", Lines(2));
        var processing = harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        var blocker = harness.BlockingIngestor.ShouldNotBeNull();
        await blocker.Entered.WaitAsync(TestContext.Current.CancellationToken);
        blocker.ReleaseWith(new InvalidOperationException("the archive is unreachable"));
        await Should.ThrowAsync<InvalidOperationException>(() => processing);

        var first = harness.InboxNames().ShouldHaveSingleItem();
        first.ShouldStartWith("keeps-failing.retry-");

        // Vòng hai và vòng ba fail theo cùng cách, vì blocker vẫn đang ở trạng thái fail.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.ProcessPathAsync(
                Path.Combine(harness.InboxPath, first),
                TestContext.Current.CancellationToken));

        var second = harness.InboxNames().ShouldHaveSingleItem();
        second.ShouldStartWith("keeps-failing.retry2-");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.ProcessPathAsync(
                Path.Combine(harness.InboxPath, second),
                TestContext.Current.CancellationToken));

        var third = harness.InboxNames().ShouldHaveSingleItem();
        third.ShouldStartWith("keeps-failing.retry3-");

        // Cái tên nói lên nó đã trải qua bao nhiêu vòng, và không có gì khác lớn thêm.
        third.Length.ShouldBe(second.Length);
        third.Split(".retry").Length.ShouldBe(2);
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task AClaimCancelledBeforeItIsRead_ComesBackPublishedRatherThanStayingClaimed()
    {
        // Trước đây, việc restore một claim mà bytes của nó chưa từng được đọc có thể throw ra khỏi
        // catch block, thay thế failure vốn là nguyên nhân gây ra retry và để export nằm lại dưới
        // .processing cho tới một lần restart mà không ai lên lịch.
        await using var harness = await FileDropHarness.StartAsync();

        var published = await harness.WriteDropAsync("host-stopping.csv", Lines(2));
        var original = await File.ReadAllBytesAsync(published, TestContext.Current.CancellationToken);

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            harness.ProcessPathAsync(published, stopping.Token));

        var restored = harness.InboxNames().ShouldHaveSingleItem();
        restored.ShouldStartWith("host-stopping.retry-");
        restored.ShouldEndWith(".csv.ready");

        (await File.ReadAllBytesAsync(
            Path.Combine(harness.InboxPath, restored),
            TestContext.Current.CancellationToken)).ShouldBe(original);
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();
        (await harness.CountTelemetryAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TheSameFileDroppedTwice_DoesNotChangeTheRowCount()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var first = await harness.DropAsync("eol-export.csv", Lines(3));
        first.ShouldNotBeNull();
        first.Inserted.ShouldBe(3);
        (await harness.CountTelemetryAsync()).ShouldBe(3);

        // Đúng từng byte cùng một export. Natural key là một hàm của những gì đã được đo, không phải
        // của thời điểm file tới, nên không có gì mới được lưu.
        var second = await harness.DropAsync("eol-export.csv", Lines(3));
        second.ShouldNotBeNull();
        second.Inserted.ShouldBe(0);
        second.Duplicates.ShouldBe(3);
        (await harness.CountTelemetryAsync()).ShouldBe(3);

        harness.Processed().Count.ShouldBe(1);
    }

    [Fact]
    public async Task OneBadLineIsRejectedAlone_AndTheOtherNinetyNineAreStored()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var lines = Lines(100).ToList();
        lines[50] = lines[50].Replace(",real,4.", ",real,not-a-number-4.", StringComparison.Ordinal);

        var result = await harness.DropAsync("eol-export.csv", lines);

        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(99);
        (await harness.CountTelemetryAsync()).ShouldBe(99);

        // Bản thân file thành công; chỉ dòng đó fail.
        harness.Processed().ShouldHaveSingleItem().ShouldBe("eol-export.csv");

        var rejected = harness.Rejected();
        rejected.ShouldContain("eol-export.line-52.csv");
        rejected.ShouldContain("eol-export.line-52.csv.error");

        var reason = await harness.ReadRejectedAsync("eol-export.line-52.csv.error");
        reason.ShouldContain("not-a-number");
        reason.ShouldContain("The other lines of the file were stored");

        // Header đi kèm với dòng bị reject: một hàng chỉ toàn dấu phẩy là thứ mà operator phải giải
        // mã bằng cách đếm.
        (await harness.ReadRejectedAsync("eol-export.line-52.csv"))
            .ShouldStartWith(CsvMeasurementReader.Header);
    }

    [Fact]
    public async Task AFileWithNoHeader_IsRejectedWholeAndStoresNothing()
    {
        await using var harness = await FileDropHarness.StartAsync();

        var result = await harness.DropAsync("headerless.csv", [Row(1)], writeHeader: false);

        result.ShouldBeNull();
        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Processed().ShouldBeEmpty();
        harness.Rejected().ShouldContain("headerless.csv");
        (await harness.ReadRejectedAsync("headerless.csv.error")).ShouldContain("header");
    }

    [Fact]
    public async Task FileDropRows_AreStoredWithClockQualityUnknown()
    {
        // Trường hợp mà Sparkplug path không thể tạo ra. C02 từ chối một metric không có timestamp vì
        // device_timestamp nằm trong natural key; ở đây CSV mang theo một thời điểm đo, nên key vẫn
        // hoạt động — nhưng không có đồng hồ thiết bị nào tham gia cả, nên gọi nó là Good sẽ là một
        // khẳng định không ai đưa ra. Đây là giá trị thứ ba của scope.md §7.3 tới từ đúng nguồn thật
        // của nó.
        await using var harness = await FileDropHarness.StartAsync();

        await harness.DropAsync("eol-export.csv", Lines(2));

        var qualities = await harness.ReadClockQualitiesAsync();

        qualities.ShouldBe(["Unknown", "Unknown"]);
    }

    private static List<string> Lines(int count) =>
        [.. Enumerable.Range(1, count).Select(index => Row(index))];

    private sealed class FileDropHarness : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly NpgsqlDataSource _dataSource;
        private readonly FileDropOptions _options;
        private readonly FileDropProcessor _processor;
        private readonly string _root;

        private FileDropHarness(
            PostgreSqlContainer postgres,
            NpgsqlDataSource dataSource,
            FileDropOptions options,
            FileDropProcessor processor,
            RecordingRawCurveArchive archive,
            string root)
        {
            _postgres = postgres;
            _dataSource = dataSource;
            _options = options;
            _processor = processor;
            _root = root;
            Archive = archive;
        }

        internal RecordingRawCurveArchive Archive { get; }

        internal BlockingMeasurementIngestor? BlockingIngestor { get; private init; }

        internal string ProcessedPath => _options.ProcessedPath;

        internal string InboxPath => _options.InboxPath;

        internal string ProcessingPath => Path.Combine(_options.InboxPath, ".processing");

        internal string DataPath(string fileName) => Path.Combine(_options.InboxPath, fileName);

        internal string PublishedPathFor(string dataFilePath) =>
            dataFilePath + _options.PublishedSuffix;

        internal static async Task<FileDropHarness> StartAsync(
            bool blockFileIngestion = false,
            ILogger<FileDropProcessor>? logger = null)
        {
            var postgres = new PostgreSqlBuilder("timescale/timescaledb:2.29.2-pg17")
                .WithDatabase("novavolt_integration")
                .WithUsername("nvm")
                .WithPassword("nvm_integration_only")
                .Build();

            await postgres.StartAsync(CancellationToken.None);
            IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

            var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
            var root = Path.Combine(Path.GetTempPath(), "nvm-file-drop-tests", Guid.NewGuid().ToString("N"));

            var options = new FileDropOptions
            {
                Enabled = true,
                InboxPath = Path.Combine(root, "inbox"),
                ProcessedPath = Path.Combine(root, "processed"),
                RejectedPath = Path.Combine(root, "rejected"),
                SettleTime = TimeSpan.Zero,
            };
            options.Validate();

            var clock = new FakeTimeProvider(ReadAt);
            var archive = new RecordingRawCurveArchive();
            IMeasurementIngestor ingestor = new PostgresMeasurementIngestor(
                dataSource,
                clock,
                new IngestionMetrics());
            BlockingMeasurementIngestor? blockingIngestor = null;

            if (blockFileIngestion)
            {
                blockingIngestor = new BlockingMeasurementIngestor(ingestor);
                ingestor = blockingIngestor;
            }

            var processor = new FileDropProcessor(
                new CsvMeasurementReader(new OneChannelDirectory()),
                ingestor,
                options,
                clock,
                logger ?? NullLogger<FileDropProcessor>.Instance,
                archive);

            processor.EnsureDirectories();

            return new FileDropHarness(postgres, dataSource, options, processor, archive, root)
            {
                BlockingIngestor = blockingIngestor,
            };
        }

        internal async Task<IngestionResult?> DropAsync(
            string fileName,
            IReadOnlyList<string> rows,
            bool writeHeader = true)
        {
            var path = await WriteDropAsync(fileName, rows, writeHeader);
            return await ProcessPathAsync(path, TestContext.Current.CancellationToken);
        }

        internal Task<string> WriteDropAsync(
            string fileName,
            IReadOnlyList<string> rows,
            bool writeHeader = true)
        {
            var lines = writeHeader ? [CsvMeasurementReader.Header, .. rows] : rows;
            return PublishAsync(DataPath(fileName), lines);
        }

        /// <summary>Drop một file theo đúng cách mà publish contract yêu cầu một producer phải làm.</summary>
        /// <remarks>
        /// Mọi test đều đi qua đây, nên tất cả chúng đều thực thi contract chứ không chỉ riêng test
        /// nói về nó: ghi một tên tạm, đóng nó lại, rồi rename nó vào đúng chỗ trong một bước. Trả về
        /// published path, cái tên duy nhất mà adapter sẽ nhìn vào.
        /// </remarks>
        internal async Task<string> PublishAsync(string dataFilePath, IReadOnlyList<string> lines)
        {
            var staging = dataFilePath + ".partial";
            await File.WriteAllLinesAsync(staging, lines, TestContext.Current.CancellationToken);

            var published = PublishedPathFor(dataFilePath);
            File.Move(staging, published, overwrite: true);

            return published;
        }

        internal void RestartWatcher() => _processor.EnsureDirectories();

        internal IReadOnlyList<string> InboxNames() =>
            [.. Directory.EnumerateFiles(_options.InboxPath).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

        internal Task<IngestionResult?> ProcessPathAsync(string path, CancellationToken cancellationToken) =>
            _processor.ProcessAsync(path, cancellationToken);

        internal IReadOnlyList<string> Processed() =>
            [.. Directory.EnumerateFiles(_options.ProcessedPath).Select(Path.GetFileName).OfType<string>()];

        internal IReadOnlyList<string> Rejected() =>
            [.. Directory.EnumerateFiles(_options.RejectedPath).Select(Path.GetFileName).OfType<string>()];

        internal Task<string> ReadRejectedAsync(string fileName) =>
            File.ReadAllTextAsync(
                Path.Combine(_options.RejectedPath, fileName),
                TestContext.Current.CancellationToken);

        internal async Task<(DateTimeOffset First, DateTimeOffset Last)> MeasuredSpanAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT min(device_timestamp), max(device_timestamp) FROM ts.telemetry_measurement;",
                connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

            return (
                await reader.GetFieldValueAsync<DateTimeOffset>(0, TestContext.Current.CancellationToken),
                await reader.GetFieldValueAsync<DateTimeOffset>(1, TestContext.Current.CancellationToken));
        }

        internal async Task<long> CountTelemetryAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM ts.telemetry_measurement;",
                connection);

            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        internal async Task<IReadOnlyList<string>> ReadClockQualitiesAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT clock_quality FROM ts.telemetry_measurement ORDER BY device_timestamp;",
                connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            var qualities = new List<string>();

            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                qualities.Add(reader.GetString(0));
            }

            return qualities;
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _postgres.DisposeAsync();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class OneChannelDirectory : IEquipmentDirectory
    {
        public bool Contains(EquipmentPath path) =>
            path == Line || path == Channel || path == OtherChannel;

        public EquipmentPath? FindDevice(EquipmentPath line, string deviceCode)
        {
            if (line != Line)
            {
                return null;
            }

            if (string.Equals(deviceCode, Channel.Code, StringComparison.Ordinal))
            {
                return Channel;
            }

            return string.Equals(deviceCode, OtherChannel.Code, StringComparison.Ordinal)
                ? OtherChannel
                : null;
        }
    }

    private sealed class BlockingMeasurementIngestor(IMeasurementIngestor inner)
        : IMeasurementIngestor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Exception? _failure;

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        /// <summary>Cho phép ingest tiếp tục rồi fail, giống cách một transaction bị rollback vẫn làm.</summary>
        internal void ReleaseWith(Exception failure)
        {
            _failure = failure;
            _release.TrySetResult();
        }

        public Task<IngestionResult> IngestAsync(
            IReadOnlyCollection<DecodedSparkplugMessage> messages,
            CancellationToken cancellationToken) =>
            inner.IngestAsync(messages, cancellationToken);

        public async Task<IngestionResult> IngestAsync(
            IReadOnlyCollection<FileMeasurement> measurements,
            DateTimeOffset gatewayTimestamp,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);

            if (_failure is not null)
            {
                throw _failure;
            }

            return await inner.IngestAsync(measurements, gatewayTimestamp, cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        internal IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add(new LogEntry(eventId, formatter(state, exception)));

        internal sealed record LogEntry(EventId EventId, string Message);
    }

    /// <summary>Một archive giữ lại bất cứ thứ gì được đưa cho nó, để phần wiring có thể được assert.</summary>
    /// <remarks>
    /// Là một fake thay vì MinIO, có chủ đích. RawCurveArchiveTests đã chứng minh digest, object lock
    /// và tính idempotent trên một S3 thật rồi; câu hỏi còn bỏ ngỏ mà file này trả lời là adapter
    /// đang chạy có gọi tới bất kỳ cái nào trong số đó hay không, và câu trả lời cho điều đó không
    /// nên cần tới một container.
    /// </remarks>
    internal sealed class RecordingRawCurveArchive : IRawCurveArchive
    {
        private readonly List<ArchiveCall> _calls = [];

        internal IReadOnlyList<ArchiveCall> Calls => _calls;

        public async Task<RawCurveArchiveResult> ArchiveAsync(
            RawCurveDescriptor descriptor,
            Stream source,
            RawCurveProvenance provenance,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            _calls.Add(new ArchiveCall(descriptor, provenance, buffer.ToArray()));

            return new RawCurveArchiveResult(
                Guid.NewGuid(),
                "test/object",
                "test-version",
                new string('0', 64),
                buffer.Length,
                ObjectCreated: true,
                MetadataCreated: true);
        }

        internal sealed record ArchiveCall(
            RawCurveDescriptor Descriptor,
            RawCurveProvenance Provenance,
            byte[] Bytes);
    }
}
