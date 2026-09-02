using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion.Persistence;
using Nvm.Ingestion.RawCurves;
using Nvm.Kernel.Identity;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Nvm.IntegrationTests;

/// <summary>C12: một file máy gốc vẫn là một WORM object và một metadata row có thể kiểm chứng.</summary>
public sealed class RawCurveArchiveTests
{
    private const string MinioImage = "minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const string BucketName = "raw-curve";
    private const string AccessKey = "nvm-integration";
    private const string SecretKey = "NvmIntegration!2026";

    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Original = Encoding.UTF8.GetBytes(
        """
        equipment_path,unit_id,signal_code,measured_at,value_kind,value
        NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,NV1-C-260831-000142,Formation/Voltage,2026-08-31T06:00:00.000Z,real,3.100
        NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,NV1-C-260831-000142,Formation/Current,2026-08-31T06:00:00.000Z,real,0.500
        NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142,NV1-C-260831-000142,Formation/Voltage,2026-08-31T06:01:00.000Z,real,3.120
        """);

    [Fact]
    public async Task OriginalBytes_AreIdempotentVerifiableAndComplianceLocked()
    {
        await using var harness = await RawCurveHarness.StartAsync();
        var descriptor = new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            "NV1-C-260831-000142",
            new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 6, 2, 0, TimeSpan.Zero));

        await using var firstSource = new MemoryStream(Original, writable: false);
        var first = await harness.Store.ArchiveAsync(
            descriptor,
            firstSource,
            new RawCurveProvenance("tester:jo", "Original export from FORM-01-CH-0142"),
            TestContext.Current.CancellationToken);
        await using var secondSource = new MemoryStream(Original, writable: false);
        var second = await harness.Store.ArchiveAsync(
            descriptor,
            secondSource,
            new RawCurveProvenance("tester:someone-else", "Re-uploaded the same export by mistake"),
            TestContext.Current.CancellationToken);

        first.ObjectCreated.ShouldBeTrue();
        first.MetadataCreated.ShouldBeTrue();
        first.Sha256.Length.ShouldBe(64);
        first.ByteSize.ShouldBe(Original.LongLength);

        second.ShouldBe(first with { ObjectCreated = false, MetadataCreated = false });
        (await harness.CountRowsAsync("NV1", first.ArchiveId)).ShouldBe(1);
        (await harness.CountRowsAsync("DE1", first.ArchiveId)).ShouldBe(0);

        var versions = await harness.S3.ListVersionsAsync(
            new ListVersionsRequest { BucketName = BucketName, Prefix = first.ObjectKey },
            TestContext.Current.CancellationToken);
        versions.Versions.ShouldNotBeNull();
        versions.Versions.Count(version =>
            string.Equals(version.Key, first.ObjectKey, StringComparison.Ordinal)).ShouldBe(1);

        using var downloaded = await harness.S3.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = BucketName,
                Key = first.ObjectKey,
                VersionId = first.ObjectVersionId,
            },
            TestContext.Current.CancellationToken);
        await using var downloadedBytes = new MemoryStream();
        await downloaded.ResponseStream.CopyToAsync(
            downloadedBytes,
            TestContext.Current.CancellationToken);
        downloadedBytes.ToArray().ShouldBe(Original);
        downloadedBytes.Position = 0;
        (await RawCurveDigest.MatchesAsync(
            downloadedBytes,
            first.Sha256,
            first.ByteSize,
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        var tampered = downloadedBytes.ToArray();
        tampered[^2] ^= 0x01;
        await using var tamperedBytes = new MemoryStream(tampered, writable: false);
        (await RawCurveDigest.MatchesAsync(
            tamperedBytes,
            first.Sha256,
            first.ByteSize,
            TestContext.Current.CancellationToken)).ShouldBeFalse();

        (await harness.Store.VerifyAsync(
            "NV1",
            first.ArchiveId,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await harness.Store.VerifyAsync(
            "DE1",
            first.ArchiveId,
            TestContext.Current.CancellationToken)).ShouldBeFalse();

        var deleteFailure = await Should.ThrowAsync<AmazonS3Exception>(() =>
            harness.S3.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = BucketName,
                    Key = first.ObjectKey,
                    VersionId = first.ObjectVersionId,
                },
                TestContext.Current.CancellationToken));
        deleteFailure.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            $"{deleteFailure.ErrorCode}: {deleteFailure.Message}");
        deleteFailure.ErrorCode.ShouldBe("InvalidRequest");
        deleteFailure.Message.ShouldContain("WORM protected");

        var updateFailure = await Should.ThrowAsync<PostgresException>(() =>
            harness.ExecuteAsync(
                "UPDATE ts.raw_curve_archive SET byte_size = byte_size + 1 WHERE site_id = 'NV1';"));
        updateFailure.SqlState.ShouldBe("P1201");

        var deleteMetadataFailure = await Should.ThrowAsync<PostgresException>(() =>
            harness.ExecuteAsync("DELETE FROM ts.raw_curve_archive WHERE site_id = 'NV1';"));
        deleteMetadataFailure.SqlState.ShouldBe("P1201");

        // K5. Row nói ai đã đưa byte này vào đây và vì sao, đồng thời giữ câu trả lời ĐẦU TIÊN: cùng
        // byte archive hai lần vẫn là một evidence, nên reason của caller thứ hai không ghi đè reason
        // của caller đầu. Archive có attribution thay đổi khi re-upload thì attribution vô nghĩa.
        (await harness.ReadAsync(
            "SELECT actor, reason, coalesce(supersedes_archive_id::text, '<null>') "
            + "FROM ts.raw_curve_archive WHERE site_id = 'NV1';"))
            .ShouldBe(["tester:jo", "Original export from FORM-01-CH-0142", "<null>"]);
    }

    [Fact]
    public async Task ACurveOfOneInstant_IsArchivedAndStaysIdempotent()
    {
        // Mọi test khác ở đây trải trên phút trọn vẹn, nên đi qua `timestamptz` không đổi. File drop
        // một reading thì không: interval của nó kết thúc một microsecond sau khi bắt đầu; phiên bản
        // kết thúc sau một tick 100 ns lại lưu thành interval bằng không. PostgreSQL từ chối row, export
        // chạy mãi trong retry loop, và không test nào phát hiện vì không test nào archive interval ngắn vậy.
        await using var harness = await RawCurveHarness.StartAsync();
        var instant = new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero);
        var descriptor = new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            "NV1-C-260831-000142",
            instant,
            instant.AddTicks(TimeSpan.TicksPerMicrosecond));

        await using var firstSource = new MemoryStream(Original, writable: false);
        var first = await harness.Store.ArchiveAsync(
            descriptor,
            firstSource,
            new RawCurveProvenance("tester:jo", "Original export of a single reading"),
            TestContext.Current.CancellationToken);

        // Lần archive thứ hai là nơi identity check so descriptor với row trả về. Descriptor mang
        // precision mà column không chứa được sẽ fail ở đây, và báo "different plant evidence" về
        // evidence giống hệt nhau.
        await using var secondSource = new MemoryStream(Original, writable: false);
        var second = await harness.Store.ArchiveAsync(
            descriptor,
            secondSource,
            new RawCurveProvenance("tester:jo", "The same export, dropped in again"),
            TestContext.Current.CancellationToken);

        first.ObjectCreated.ShouldBeTrue();
        second.ShouldBe(first with { ObjectCreated = false, MetadataCreated = false });
        (await harness.CountRowsAsync("NV1", first.ArchiveId)).ShouldBe(1);
    }

    [Fact]
    public void AnIntervalThatOnlyExistsBelowAMicrosecond_IsRefusedWhereItIsBuilt()
    {
        // Rule nằm trong descriptor thay vì caller, để caller kế tiếp không thể đưa lại interval mà
        // archive không lưu được.
        var instant = new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero);

        Should.Throw<ArgumentException>(() => new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            "NV1-C-260831-000142",
            instant,
            instant.AddTicks(1)));

        var kept = new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            "NV1-C-260831-000142",
            instant.AddTicks(3),
            instant.AddTicks(TimeSpan.TicksPerMicrosecond + 7));

        // Round down về thứ column chứa được ở cả hai đầu, nên row trả về chính là descriptor đã ghi nó.
        kept.CurveStartAt.ShouldBe(instant);
        kept.CurveEndAt.ShouldBe(instant.AddTicks(TimeSpan.TicksPerMicrosecond));
    }

    [Fact]
    public async Task ACorrection_IsANewRowThatNamesItsAuthorAndWhatItReplaces()
    {
        // Hình dạng K5 yêu cầu, và migration 006 mới build một nửa: record sai vẫn ở đó, đọc và kiểm
        // chứng được, correction đứng cạnh nó nói điều gì sai. Đến nay thiếu mọi phần của câu đó, trừ
        // "đứng cạnh".
        await using var harness = await RawCurveHarness.StartAsync();
        var descriptor = new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            "NV1-C-260831-000142",
            new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 6, 2, 0, TimeSpan.Zero));

        await using var wrongSource = new MemoryStream(Original, writable: false);
        var wrong = await harness.Store.ArchiveAsync(
            descriptor,
            wrongSource,
            new RawCurveProvenance("tester:jo", "Original export"),
            TestContext.Current.CancellationToken);

        var correctedBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Original).Replace("3.120", "3.121", StringComparison.Ordinal));
        await using var correctedSource = new MemoryStream(correctedBytes, writable: false);
        var corrected = await harness.Store.ArchiveAsync(
            descriptor,
            correctedSource,
            new RawCurveProvenance(
                "engineer:mai",
                "Re-exported after the tester's decimal separator was fixed",
                wrong.ArchiveId),
            TestContext.Current.CancellationToken);

        corrected.ArchiveId.ShouldNotBe(wrong.ArchiveId);
        corrected.Sha256.ShouldNotBe(wrong.Sha256);

        (await harness.ReadAsync(
            "SELECT actor, reason, supersedes_archive_id::text "
            + "FROM ts.raw_curve_archive WHERE supersedes_archive_id IS NOT NULL;"))
            .ShouldBe(
            [
                "engineer:mai",
                "Re-exported after the tester's decimal separator was fixed",
                wrong.ArchiveId.ToString(),
            ]);

        // Evidence đã bị thay thế vẫn ở đó và kiểm chứng được. Correction xóa nó sẽ trả lời "hệ thống
        // nói gì bây giờ" và phá hủy "nó đã nói gì trước đây".
        (await harness.CountRowsAsync("NV1", wrong.ArchiveId)).ShouldBe(1);
        (await harness.Store.VerifyAsync(
            "NV1",
            wrong.ArchiveId,
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Hai row cũng không thể cùng nhận là correction của một row, vì sẽ để auditor có hai câu trả
        // lời mà không có cách chọn.
        await using var thirdSource = new MemoryStream(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Original).Replace("3.120", "3.122", StringComparison.Ordinal)),
            writable: false);
        var duplicateCorrection = await Should.ThrowAsync<PostgresException>(() =>
            harness.Store.ArchiveAsync(
                descriptor,
                thirdSource,
                new RawCurveProvenance("engineer:mai", "Second attempt at the same fix", wrong.ArchiveId),
                TestContext.Current.CancellationToken));

        duplicateCorrection.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task ACorrectionCannotReachAcrossSites()
    {
        // K3 và K5 đồng thời, nhưng đến migration 012 schema chỉ ép một nửa. Migration 009 liên kết
        // correction với thứ nó thay bằng foreign key chỉ trên archive_id, nên row ở NV1 có thể tự nhận
        // là correction của row ở DE1 — evidence chain băng qua ranh giới lẽ ra là security boundary,
        // trong khi cả hai rule đều tự báo đã thỏa.
        //
        // Independent audit tìm ra ngày 2026-09-01, ngay trong migration viết để khép nửa K5 của table
        // này. RawCurveArchiveStore truyền thẳng id từ caller, nên constraint phải nằm trong schema;
        // check ở store chỉ bind caller duy nhất tồn tại hôm nay.
        await using var harness = await RawCurveHarness.StartAsync();

        await using var deSource = new MemoryStream(Original, writable: false);
        var atDe1 = await harness.Store.ArchiveAsync(
            new RawCurveDescriptor(
                EquipmentPath.Parse("NOVAVOLT/DE1/PACK/P1/PACK-01/PACK-01-ST-0001"),
                "DE1-P-260831-000001",
                new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 31, 6, 2, 0, TimeSpan.Zero)),
            deSource,
            new RawCurveProvenance("tester:jo", "Original export at the other plant"),
            TestContext.Current.CancellationToken);

        await using var nvSource = new MemoryStream(Original, writable: false);
        var crossSite = await Should.ThrowAsync<PostgresException>(() =>
            harness.Store.ArchiveAsync(
                new RawCurveDescriptor(
                    EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
                    "NV1-C-260831-000142",
                    new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 8, 31, 6, 2, 0, TimeSpan.Zero)),
                nvSource,
                new RawCurveProvenance(
                    "engineer:mai",
                    "Claims to correct a record at another plant",
                    atDe1.ArchiveId),
                TestContext.Current.CancellationToken));

        crossSite.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // Record DE1 không bị đụng tới. Correction bị từ chối không được làm hỏng thứ nó trỏ vào.
        (await harness.CountRowsAsync("DE1", atDe1.ArchiveId)).ShouldBe(1);
    }

    [Fact]
    public async Task AnArchiveWithoutAReason_IsRefusedBeforeAnythingIsWritten()
    {
        await using var harness = await RawCurveHarness.StartAsync();
        var descriptor = new RawCurveDescriptor(
            EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142"),
            null,
            new DateTimeOffset(2026, 8, 31, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 31, 6, 2, 0, TimeSpan.Zero));

        await using var source = new MemoryStream(Original, writable: false);

        await Should.ThrowAsync<ArgumentException>(() =>
            harness.Store.ArchiveAsync(
                descriptor,
                source,
                new RawCurveProvenance("tester:jo", "   "),
                TestContext.Current.CancellationToken));

        // Không gì cả, kể cả object: reason trống bị bắt trước upload, nên archive bị từ chối không để
        // orphan dưới compliance lock không thể xóa.
        (await harness.ReadAsync("SELECT count(*)::text FROM ts.raw_curve_archive;")).ShouldBe(["0"]);
    }

    [Fact]
    public async Task DownScript_RemovesOnlyTheRawCurveIndex()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());

        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("009_raw_curve_correction_trail"),
                TestContext.Current.CancellationToken));
        await TelemetryHypertableTests.ExecuteAsync(
            dataSource,
            await File.ReadAllTextAsync(
                TelemetryHypertableTests.DownScriptPath("006_raw_curve_archive"),
                TestContext.Current.CancellationToken));

        (await TelemetryHypertableTests.ReadAsync(
            dataSource,
            "SELECT to_regclass('ts.raw_curve_archive')::text, to_regclass('ts.process_signal_1m')::text;"))
            .ShouldBe(["<null>", "ts.process_signal_1m"]);
    }

    private sealed class RawCurveHarness : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly MinioContainer _minio;
        private readonly NpgsqlDataSource _dataSource;

        private RawCurveHarness(
            PostgreSqlContainer postgres,
            MinioContainer minio,
            NpgsqlDataSource dataSource,
            IAmazonS3 s3,
            RawCurveArchiveStore store)
        {
            _postgres = postgres;
            _minio = minio;
            _dataSource = dataSource;
            S3 = s3;
            Store = store;
        }

        internal IAmazonS3 S3 { get; }

        internal RawCurveArchiveStore Store { get; }

        internal static async Task<RawCurveHarness> StartAsync()
        {
            var minio = new MinioBuilder(MinioImage)
                .WithUsername(AccessKey)
                .WithPassword(SecretKey)
                .Build();
            await minio.StartAsync(TestContext.Current.CancellationToken);

            PostgreSqlContainer? postgres = null;
            NpgsqlDataSource? dataSource = null;
            AmazonS3Client? s3 = null;

            try
            {
                postgres = await TelemetryHypertableTests.StartAsync();
                IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());
                dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
                s3 = new AmazonS3Client(
                    new BasicAWSCredentials(minio.GetAccessKey(), minio.GetSecretKey()),
                    new AmazonS3Config
                    {
                        ServiceURL = minio.GetConnectionString(),
                        ForcePathStyle = true,
                        AuthenticationRegion = "us-east-1",
                    });

                await s3.PutBucketAsync(
                    new PutBucketRequest
                    {
                        BucketName = BucketName,
                        ObjectLockEnabledForBucket = true,
                    },
                    TestContext.Current.CancellationToken);
                await s3.PutObjectLockConfigurationAsync(
                    new PutObjectLockConfigurationRequest
                    {
                        BucketName = BucketName,
                        ObjectLockConfiguration = new ObjectLockConfiguration
                        {
                            ObjectLockEnabled = ObjectLockEnabled.Enabled,
                            Rule = new ObjectLockRule
                            {
                                DefaultRetention = new DefaultRetention
                                {
                                    Mode = ObjectLockRetentionMode.Compliance,
                                    Years = 15,
                                },
                            },
                        },
                    },
                    TestContext.Current.CancellationToken);

                var lockConfiguration = await s3.GetObjectLockConfigurationAsync(
                    new GetObjectLockConfigurationRequest { BucketName = BucketName },
                    TestContext.Current.CancellationToken);
                lockConfiguration.ObjectLockConfiguration.ObjectLockEnabled.Value.ShouldBe("Enabled");
                lockConfiguration.ObjectLockConfiguration.Rule.DefaultRetention.Mode.Value
                    .ShouldBe("COMPLIANCE");
                lockConfiguration.ObjectLockConfiguration.Rule.DefaultRetention.Years.ShouldBe(15);

                var store = new RawCurveArchiveStore(
                    dataSource,
                    s3,
                    new FakeTimeProvider(RecordedAt),
                    BucketName);

                return new RawCurveHarness(postgres, minio, dataSource, s3, store);
            }
            catch
            {
                s3?.Dispose();

                if (dataSource is not null)
                {
                    await dataSource.DisposeAsync();
                }

                if (postgres is not null)
                {
                    await postgres.DisposeAsync();
                }

                await minio.DisposeAsync();
                throw;
            }
        }

        [SuppressMessage(
            "Security",
            "CA2100:Review SQL queries for security vulnerabilities",
            Justification =
                "Every caller passes a literal written in this file; no value comes from outside the "
                + "test assembly.")]
        internal async Task<string[]> ReadAsync(string sql)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(
                TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

            (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

            var values = new string[reader.FieldCount];

            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = await reader.IsDBNullAsync(index, TestContext.Current.CancellationToken)
                    ? "<null>"
                    : reader.GetValue(index).ToString() ?? string.Empty;
            }

            return values;
        }

        internal async Task<long> CountRowsAsync(string siteId, Guid archiveId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM ts.raw_curve_archive WHERE site_id = @site_id AND archive_id = @archive_id;",
                connection);
            command.Parameters.AddWithValue("site_id", siteId);
            command.Parameters.AddWithValue("archive_id", archiveId);
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        [SuppressMessage(
            "Security",
            "CA2100:Review SQL queries for security vulnerabilities",
            Justification =
                "Both callers pass fixed mutation literals declared in this test; no external value reaches SQL.")]
        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            S3.Dispose();
            await _dataSource.DisposeAsync();
            await _minio.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }
}
