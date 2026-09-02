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
/// C15: an end-of-line tester that has never heard of MQTT still comes in through the same door.
/// The two adapters differ in how they read and in nothing else — same natural key, same claim, same
/// transaction — because two dedup definitions drift within months and the symptom is one
/// measurement stored twice for exactly the machines that report through both routes.
/// </summary>
public sealed class FileDropTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly EquipmentPath Line = EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1");
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");
    private static readonly EquipmentPath OtherChannel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143");

    // Interpolation rather than string.Format: one reading per index, and the analyzers are right
    // that a format string used in a loop wants a CompositeFormat it does not need here.
    private static string Row(int index) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,,Formation/CapacityResult,2026-08-28T09:28:11.{index:000}Z,real,4.8{index:000}");

    [Fact]
    public async Task TheOriginalBytes_AreArchivedBeforeTheFileIsFiledAsProcessed()
    {
        // C12 had a WORM store with no caller: ArchiveAsync was reachable only from its own test, so
        // a running plant kept measurements and threw the machine's own export away. This is the wire.
        //
        // What is asserted is the shape of the claim an auditor would test: the exact bytes on disk,
        // the machine they came from, the interval they cover, and — K5 — who handed them over and
        // why. The digest and the object lock belong to RawCurveArchiveTests; here the question is
        // only whether anything calls it at all.
        await using var harness = await FileDropHarness.StartAsync();

        var result = await harness.DropAsync("eol-export.csv", Lines(3));

        result.ShouldNotBeNull();
        var archived = harness.Archive.Calls.ShouldHaveSingleItem();
        archived.Descriptor.EquipmentPath.ShouldBe(Channel);
        archived.Descriptor.SiteId.ShouldBe("NV1");
        archived.Provenance.Actor.ShouldBe("ingestion:file-drop");
        archived.Provenance.Reason.ShouldContain("eol-export.csv");
        archived.Provenance.SupersedesArchiveId.ShouldBeNull();

        // Byte for byte what is on disk, not a re-serialisation of the parsed rows. Everything the
        // archive exists for depends on this being the file the machine wrote.
        archived.Bytes.ShouldBe(
            await File.ReadAllBytesAsync(
                Path.Combine(harness.ProcessedPath, "eol-export.csv"),
                TestContext.Current.CancellationToken));

        // The interval is read off the samples, not off the file name, and it is half-open like every
        // other interval here: the last sample is inside it by exactly one microsecond. A tick would
        // read better and cannot be stored — the archive keeps the interval in a `timestamptz`, and
        // an end that only exists at 100 ns resolution comes back as a different interval than the
        // one that was written. This assertion pinned the tick until a probe on the running stack
        // showed what it cost.
        var span = await harness.MeasuredSpanAsync();
        archived.Descriptor.CurveStartAt.ShouldBe(span.First);
        archived.Descriptor.CurveEndAt.ShouldBe(span.Last.AddTicks(TimeSpan.TicksPerMicrosecond));

        // And the file only reaches `processed` once its original is kept.
        harness.Processed().ShouldHaveSingleItem().ShouldBe("eol-export.csv");
    }

    [Fact]
    public async Task AFileNamingTwoMachines_IsRejectedWholeAndStoresNothing()
    {
        // An export is one machine's record of one run. A file mixing two channels has no single
        // answer to "whose curve is this", so it can never have an original — and a measurement whose
        // original cannot be produced is not evidence (K4).
        //
        // The first version of this fix stored the rows, logged a warning and filed the file under
        // `processed`. That contradicted the guarantee stated three lines above it in the same method,
        // and it is the exact failure mode this adapter exists to prevent: a path that looks handled.
        // Rejecting is a whole-file decision, so it happens before the first row is stored.
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

        // Split into two exports, the same three readings go in. The rejection is about the shape of
        // the file, not about the data being unusable — otherwise it would just be data loss with an
        // explanation attached.
        (await harness.DropAsync("ch-0142.csv", Lines(2))).ShouldNotBeNull();
        (await harness.DropAsync("ch-0143.csv", [mixed[^1]])).ShouldNotBeNull();
        (await harness.CountTelemetryAsync()).ShouldBe(3);
        harness.Archive.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AFileWhoseSecondMachineOnlyAppearsOnAnUnparseableLine_IsStillRejectedWhole()
    {
        // The hole the previous fix left open, found by an independent audit on 2026-09-01.
        //
        // The single-machine check used to read the machines off the measurements that parsed. A line
        // can fail on its VALUE and still name its machine perfectly clearly, so a file holding good
        // rows for CH-0142 and one broken row for CH-0143 named two machines and looked like one: the
        // CH-0142 rows were stored, the whole file was archived as CH-0142's original, and it was
        // filed under `processed`. An auditor pulling that original would be handed bytes containing
        // another machine's readings.
        await using var harness = await FileDropHarness.StartAsync();

        var mixed = new List<string>(Lines(2))
        {
            // Valid path, valid timestamp, value that is not a number.
            "NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143,,Formation/CapacityResult,"
            + "2026-08-28T09:28:12.000Z,real,not-a-number",
        };

        (await harness.DropAsync("value-broken-second-machine.csv", mixed)).ShouldBeNull();

        (await harness.CountTelemetryAsync()).ShouldBe(0);
        harness.Archive.Calls.ShouldBeEmpty();
        harness.Processed().ShouldBeEmpty();

        var reason = await harness.ReadRejectedAsync("value-broken-second-machine.csv.error");
        reason.ShouldContain("names 2 machines");

        // The rejection names both, including the one that only ever appeared on a failing line —
        // otherwise an operator reading the error cannot tell which file to split.
        reason.ShouldContain("FORM-01-CH-0142");
        reason.ShouldContain("FORM-01-CH-0143");
    }

    [Fact]
    public async Task AFileWhoseEveryDataLineFails_IsFiledUnderRejectedRatherThanProcessed()
    {
        // One machine, no ambiguity, and not one readable reading. Nothing is stored, so nothing
        // lacks its original and K4 is not violated — but the file used to land in `processed`,
        // whose name says the readings in it went in. An operator scanning that directory could not
        // tell this apart from an export that worked, and the explanation sat in another directory.
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
        // An empty export is valid CSV but not a successful production outcome. There is no machine
        // to attribute an immutable original to, and marking it processed would hide the common
        // failure where an exporter wrote its header after losing the run it was meant to export.
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
        // Stricter than "one bad line does not reject the file", and deliberately so. A bad VALUE
        // costs one measurement. A bad IDENTITY costs the ability to say whose file this is, and an
        // archive filed under a machine that might not own every line in it is not evidence.
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
        // The old implementation parsed A, released the file, wrote the rows, and then reopened the
        // public path for archival. An exporter replacing that path while ingestion was blocked made
        // the database describe A while the WORM object and `processed` both claimed B was original.
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

        // B is a genuinely new drop and remains for the next poll; completing A must not overwrite it.
        // It also has to still be *published*: claiming A took A's readiness with it in one rename,
        // so there is nothing of B's for A's claim to have consumed.
        (await File.ReadAllBytesAsync(publishedB, TestContext.Current.CancellationToken)).ShouldBe(bytesB);
        harness.InboxNames().ShouldHaveSingleItem().ShouldBe("replaced-during-ingest.csv.ready");
    }

    [Fact]
    public async Task AnExportNotYetPublished_IsNotClaimedAndNothingInItIsStored()
    {
        // Renaming a file out of the inbox does not close the handle its exporter still holds. On
        // NFS the exporter keeps appending to the same inode afterwards, so ingestion stores the
        // head of a run and the tail is deleted along with the claim, with nothing anywhere saying
        // so. A quiet mtime cannot tell that case from a finished file; only the producer can, and
        // the rename into the published name is how it says so.
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

        // Left exactly where it was, and not half-claimed: the export is the exporter's until the
        // rename says otherwise.
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
        // The invariant behind the one rename. An earlier version published readiness in a second
        // file beside the data and took the two in sequence; every leftover of that pair was a flag
        // in the inbox with nothing behind it, and the next export to arrive under that name
        // inherited it — readable the instant it appeared, however much of it had been written.
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
        // The restore used to aim at the name the export arrived under, first with File.Exists and
        // then with an exclusive create. Neither survives a producer: a POSIX rename replaces
        // whatever is at the destination, so `mv` walks straight through a placeholder and the
        // restore then deletes the export that walked in — a file destroyed by the component whose
        // whole job is to lose nothing. Nothing on this side can defend a name a producer may use,
        // so the restore no longer aims at one.
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

        // A comes back published, under a name of its own, so the next poll reads both and
        // deduplication decides what is new — instead of one of them never having existed.
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
        // Same rule on the startup path, where it matters more: the process died holding a claim,
        // and by the time it comes back the exporter has published that name again.
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

        // Recovered means readable: a file put back unpublished would sit there for good.
        (await harness.ProcessPathAsync(recoveredPath, TestContext.Current.CancellationToken))
            .ShouldNotBeNull()
            .Inserted.ShouldBe(2);
    }

    [Fact]
    public async Task AnExportOfOneReading_ReachesProcessedLikeAnyOther()
    {
        // The most ordinary export an end-of-line tester writes, and the one that could not be
        // archived at all. `Describe` ended the interval one 100 ns tick after the single reading;
        // the archive stores it in a `timestamptz`, which keeps microseconds, so the interval
        // arrived at PostgreSQL with no duration and the row was refused. The file then went round
        // the retry loop for ever. Every test here used several readings, so none of them looked.
        await using var harness = await FileDropHarness.StartAsync();

        var published = await harness.WriteDropAsync("one-reading.csv", [Row(1)]);

        var result = await harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Inserted.ShouldBe(1);
        harness.Processed().ShouldHaveSingleItem().ShouldBe("one-reading.csv");

        // The interval has to survive the round trip through microsecond storage, so it must not
        // carry anything below a microsecond.
        var archived = harness.Archive.Calls.ShouldHaveSingleItem();
        archived.Descriptor.CurveEndAt.ShouldBeGreaterThan(archived.Descriptor.CurveStartAt);
        (archived.Descriptor.CurveEndAt.Ticks % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
        (archived.Descriptor.CurveStartAt.Ticks % TimeSpan.TicksPerMicrosecond).ShouldBe(0);
    }

    [Fact]
    public async Task AnExportThatKeepsFailing_CountsItsRoundsInsteadOfGrowingItsName()
    {
        // The retry marker used to be appended to whatever name the export arrived under, so every
        // round of the loop made the name ~40 bytes longer. After six rounds it passed the 255-byte
        // limit, the move failed, and the export was stranded under `.processing` with only a log
        // line to say so — the failure mode being that a file which cannot be processed eventually
        // cannot be returned either.
        await using var harness = await FileDropHarness.StartAsync(blockFileIngestion: true);

        var published = await harness.WriteDropAsync("keeps-failing.csv", Lines(2));
        var processing = harness.ProcessPathAsync(published, TestContext.Current.CancellationToken);

        var blocker = harness.BlockingIngestor.ShouldNotBeNull();
        await blocker.Entered.WaitAsync(TestContext.Current.CancellationToken);
        blocker.ReleaseWith(new InvalidOperationException("the archive is unreachable"));
        await Should.ThrowAsync<InvalidOperationException>(() => processing);

        var first = harness.InboxNames().ShouldHaveSingleItem();
        first.ShouldStartWith("keeps-failing.retry-");

        // Round two and three fail the same way, because the blocker stays failed.
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

        // The name says how many rounds it has had, and nothing else grows.
        third.Length.ShouldBe(second.Length);
        third.Split(".retry").Length.ShouldBe(2);
        Directory.EnumerateFileSystemEntries(harness.ProcessingPath).ShouldBeEmpty();
    }

    [Fact]
    public async Task AClaimCancelledBeforeItIsRead_ComesBackPublishedRatherThanStayingClaimed()
    {
        // The restore of a claim whose bytes were never read used to be able to throw out of the
        // catch block, replacing the failure that caused the retry and leaving the export under
        // .processing until a restart nobody had scheduled.
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

        // Byte for byte the same export. The natural key is a function of what was measured, not of
        // when the file arrived, so nothing new is stored.
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

        // The file itself succeeded; only the line failed.
        harness.Processed().ShouldHaveSingleItem().ShouldBe("eol-export.csv");

        var rejected = harness.Rejected();
        rejected.ShouldContain("eol-export.line-52.csv");
        rejected.ShouldContain("eol-export.line-52.csv.error");

        var reason = await harness.ReadRejectedAsync("eol-export.line-52.csv.error");
        reason.ShouldContain("not-a-number");
        reason.ShouldContain("The other lines of the file were stored");

        // The header travels with the rejected line: a row of commas alone is something an operator
        // has to decode by counting.
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
        // The case the Sparkplug path cannot produce. C02 refuses a metric with no timestamp because
        // device_timestamp is in the natural key; here the CSV carries a measurement time, so the key
        // works — but no device clock was ever involved, so calling it Good would be a claim nobody
        // made. This is the third value of scope.md §7.3 arriving from its real source.
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

        /// <summary>Drops a file the way the publish contract says a producer has to.</summary>
        /// <remarks>
        /// Every test goes through here, so all of them exercise the contract rather than only the
        /// one test that is about it: write a temporary name, close it, and rename it into place in
        /// one step. Returns the published path, which is the only name the adapter will look at.
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

        /// <summary>Lets the ingest go on and then fail, the way a rolled-back transaction does.</summary>
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

    /// <summary>An archive that keeps what it was handed, so the wiring can be asserted on.</summary>
    /// <remarks>
    /// A fake rather than MinIO on purpose. RawCurveArchiveTests already proves the digest, the object
    /// lock and the idempotency against a real S3; the open question this file answers is whether the
    /// running adapter calls any of it, and the answer to that should not need a container.
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
