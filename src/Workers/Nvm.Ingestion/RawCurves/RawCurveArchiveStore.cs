using System.Globalization;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using NpgsqlTypes;
using Nvm.Kernel.Identity;

namespace Nvm.Ingestion.RawCurves;

/// <summary>Ghi đúng các byte raw-curve trước, rồi mới thêm chỉ mục database theo phạm vi site của chúng.</summary>
public sealed class RawCurveArchiveStore : IRawCurveArchive
{
    /// <summary>Bucket WORM chuyên dụng do <c>minio-init</c> cấp phát.</summary>
    public const string DefaultBucketName = "raw-curve";

    private const int RequiredRetentionYears = 15;

    private const string FindSql = """
        SELECT archive_id,
               site_id,
               equipment_id,
               unit_id,
               curve_start_at,
               curve_end_at,
               object_key,
               object_version_id,
               sha256,
               byte_size,
               recorded_at,
               actor,
               reason,
               supersedes_archive_id
        FROM ts.raw_curve_archive
        WHERE site_id = @site_id
          AND archive_id = @archive_id;
        """;

    private const string InsertSql = """
        INSERT INTO ts.raw_curve_archive (
            archive_id,
            site_id,
            equipment_id,
            unit_id,
            curve_start_at,
            curve_end_at,
            object_key,
            object_version_id,
            sha256,
            byte_size,
            recorded_at,
            actor,
            reason,
            supersedes_archive_id)
        VALUES (
            @archive_id,
            @site_id,
            @equipment_id,
            @unit_id,
            @curve_start_at,
            @curve_end_at,
            @object_key,
            @object_version_id,
            @sha256,
            @byte_size,
            @recorded_at,
            @actor,
            @reason,
            @supersedes_archive_id)
        -- Named target, không phải DO NOTHING trơ trụi. Conflict duy nhất mà insert này được phép bỏ
        -- qua là khi một writer khác đã index cùng một bằng chứng, đó chính xác là ý nghĩa của
        -- archive_id: nó là v5 của đúng bộ tuple mà uq_raw_curve_archive_identity bao phủ. Bất kỳ
        -- conflict nào khác là một bất đồng thật sự — nhất là hai dòng cùng tuyên bố sửa một bản ghi —
        -- và một DO NOTHING trơ trụi sẽ biến nó thành một no-op âm thầm rồi kéo theo một "the winner
        -- cannot be read" khó hiểu.
        ON CONFLICT (archive_id) DO NOTHING
        RETURNING archive_id;
        """;

    private static readonly Guid ArchiveNamespace = DeterministicGuid.CreateVersion5(
        DeterministicGuid.DnsNamespace,
        "novavolt.example/raw-curve-archive");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IAmazonS3 _s3;
    private readonly TimeProvider _clock;
    private readonly string _bucketName;

    /// <summary>Tạo một store trên một database đã migrate và một service tương thích S3.</summary>
    public RawCurveArchiveStore(
        NpgsqlDataSource dataSource,
        IAmazonS3 s3,
        TimeProvider clock,
        string bucketName = DefaultBucketName)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

        _dataSource = dataSource;
        _s3 = s3;
        _clock = clock;
        _bucketName = bucketName;
    }

    /// <inheritdoc />
    public async Task<RawCurveArchiveResult> ArchiveAsync(
        RawCurveDescriptor descriptor,
        Stream source,
        RawCurveProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provenance);
        provenance.Validate();

        var digest = await RawCurveDigest.CalculateAsync(source, cancellationToken);
        var archiveId = CreateArchiveId(descriptor, digest.Hex);
        var objectKey = CreateObjectKey(descriptor, digest.Hex);

        await EnsureRetentionAsync(cancellationToken);

        var existing = await FindAsync(descriptor.SiteId, archiveId, cancellationToken);

        if (existing is not null)
        {
            EnsureSameIdentity(existing, descriptor, digest);
            await EnsureObjectMetadataAsync(existing, cancellationToken);
            return Result(existing, objectCreated: false, metadataCreated: false);
        }

        var (objectCreated, objectVersionId) = await PutOnceAsync(
            descriptor,
            source,
            digest,
            archiveId,
            objectKey,
            cancellationToken);
        var candidate = new RawCurveArchiveEntry(
            archiveId,
            descriptor.SiteId,
            descriptor.EquipmentPath.Value,
            descriptor.UnitId,
            descriptor.CurveStartAt,
            descriptor.CurveEndAt,
            objectKey,
            objectVersionId,
            digest.Hex,
            digest.ByteSize,
            _clock.GetUtcNow(),
            provenance.Actor,
            provenance.Reason,
            provenance.SupersedesArchiveId);
        var metadataCreated = await InsertAsync(candidate, cancellationToken);

        if (!metadataCreated)
        {
            var winner = await FindAsync(descriptor.SiteId, archiveId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Raw curve metadata conflicted but the site-scoped winner cannot be read.");

            EnsureSameIdentity(winner, descriptor, digest);
            return Result(winner, objectCreated, metadataCreated: false);
        }

        return Result(candidate, objectCreated, metadataCreated: true);
    }

    /// <summary>Tải về đúng phiên bản đã ghi cho một site và xác minh digest gốc của nó.</summary>
    public async Task<bool> VerifyAsync(
        string siteId,
        Guid archiveId,
        CancellationToken cancellationToken)
    {
        ValidateSiteId(siteId);

        var entry = await FindAsync(siteId, archiveId, cancellationToken);

        if (entry is null)
        {
            return false;
        }

        try
        {
            using var response = await _s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucketName,
                    Key = entry.ObjectKey,
                    VersionId = entry.ObjectVersionId,
                    ChecksumMode = ChecksumMode.ENABLED,
                },
                cancellationToken);

            return await RawCurveDigest.MatchesAsync(
                response.ResponseStream,
                entry.Sha256,
                entry.ByteSize,
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task EnsureRetentionAsync(CancellationToken cancellationToken)
    {
        var response = await _s3.GetObjectLockConfigurationAsync(
            new GetObjectLockConfigurationRequest { BucketName = _bucketName },
            cancellationToken);
        var configuration = response.ObjectLockConfiguration;
        var retention = configuration?.Rule?.DefaultRetention;

        if (!string.Equals(configuration?.ObjectLockEnabled?.Value, "Enabled", StringComparison.Ordinal)
            || !string.Equals(retention?.Mode?.Value, "COMPLIANCE", StringComparison.Ordinal)
            || retention?.Years != RequiredRetentionYears
            || retention?.Days is not null)
        {
            throw new InvalidOperationException(
                $"Bucket '{_bucketName}' must have default COMPLIANCE retention for "
                + $"{RequiredRetentionYears} years before it can accept a raw curve.");
        }
    }

    private async Task<(bool Created, string VersionId)> PutOnceAsync(
        RawCurveDescriptor descriptor,
        Stream source,
        RawCurveDigest digest,
        Guid archiveId,
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                InputStream = source,
                AutoCloseStream = false,
                AutoResetStreamPosition = true,
                ContentType = "text/csv",
                IfNoneMatch = "*",
                ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                ChecksumSHA256 = digest.Base64,
            };
            request.Metadata["site-id"] = descriptor.SiteId;
            request.Metadata["archive-id"] = archiveId.ToString("D", CultureInfo.InvariantCulture);
            request.Metadata["sha256"] = digest.Hex;

            var response = await _s3.PutObjectAsync(request, cancellationToken);
            var versionId = RequireVersionId(response.VersionId);

            if (response.ChecksumSHA256 is not null
                && !string.Equals(response.ChecksumSHA256, digest.Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("S3 returned a SHA-256 different from the bytes sent.");
            }

            return (true, versionId);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Một writer khác đã thắng If-None-Match. Đọc lấy người thắng; thử lại PUT sẽ tạo ra đúng
            // cái phiên bản thứ hai mà conditional creation tồn tại để ngăn chặn.
            var metadata = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = objectKey,
                    ChecksumMode = ChecksumMode.ENABLED,
                },
                cancellationToken);
            EnsureMetadata(metadata, descriptor.SiteId, digest);
            return (false, RequireVersionId(metadata.VersionId));
        }
    }

    private async Task EnsureObjectMetadataAsync(
        RawCurveArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        var metadata = await _s3.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = entry.ObjectKey,
                VersionId = entry.ObjectVersionId,
                ChecksumMode = ChecksumMode.ENABLED,
            },
            cancellationToken);

        EnsureMetadata(metadata, entry.SiteId, new RawCurveDigest(entry.Sha256, string.Empty, entry.ByteSize));
    }

    private static void EnsureMetadata(
        GetObjectMetadataResponse metadata,
        string siteId,
        RawCurveDigest digest)
    {
        if (metadata.ContentLength != digest.ByteSize
            || !string.Equals(MetadataValue(metadata, "sha256"), digest.Hex, StringComparison.Ordinal)
            || !string.Equals(MetadataValue(metadata, "site-id"), siteId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The object at the content-addressed raw-curve key does not match its site or digest metadata.");
        }
    }

    private async Task<RawCurveArchiveEntry?> FindAsync(
        string siteId,
        Guid archiveId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FindSql, connection);
        command.Parameters.AddWithValue("site_id", NpgsqlDbType.Text, siteId);
        command.Parameters.AddWithValue("archive_id", NpgsqlDbType.Uuid, archiveId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var unitId = await reader.IsDBNullAsync(3, cancellationToken)
            ? null
            : reader.GetString(3);
        var curveStartAt = await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken);
        var curveEndAt = await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken);
        var recordedAt = await reader.GetFieldValueAsync<DateTimeOffset>(10, cancellationToken);
        var supersedes = await reader.IsDBNullAsync(13, cancellationToken)
            ? (Guid?)null
            : reader.GetGuid(13);

        return new RawCurveArchiveEntry(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            unitId,
            curveStartAt,
            curveEndAt,
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            recordedAt,
            reader.GetString(11),
            reader.GetString(12),
            supersedes);
    }

    private async Task<bool> InsertAsync(
        RawCurveArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(InsertSql, connection);
        command.Parameters.AddWithValue("archive_id", NpgsqlDbType.Uuid, entry.ArchiveId);
        command.Parameters.AddWithValue("site_id", NpgsqlDbType.Text, entry.SiteId);
        command.Parameters.AddWithValue("equipment_id", NpgsqlDbType.Text, entry.EquipmentId);
        command.Parameters.AddWithValue(
            "unit_id",
            NpgsqlDbType.Text,
            entry.UnitId is null ? DBNull.Value : entry.UnitId);
        command.Parameters.AddWithValue("curve_start_at", NpgsqlDbType.TimestampTz, entry.CurveStartAt);
        command.Parameters.AddWithValue("curve_end_at", NpgsqlDbType.TimestampTz, entry.CurveEndAt);
        command.Parameters.AddWithValue("object_key", NpgsqlDbType.Text, entry.ObjectKey);
        command.Parameters.AddWithValue("object_version_id", NpgsqlDbType.Text, entry.ObjectVersionId);
        command.Parameters.AddWithValue("sha256", NpgsqlDbType.Text, entry.Sha256);
        command.Parameters.AddWithValue("byte_size", NpgsqlDbType.Bigint, entry.ByteSize);
        command.Parameters.AddWithValue("recorded_at", NpgsqlDbType.TimestampTz, entry.RecordedAt);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Text, entry.Actor);
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, entry.Reason);
        command.Parameters.AddWithValue(
            "supersedes_archive_id",
            NpgsqlDbType.Uuid,
            entry.SupersedesArchiveId is { } superseded ? superseded : DBNull.Value);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static Guid CreateArchiveId(RawCurveDescriptor descriptor, string sha256) =>
        DeterministicGuid.CreateVersion5(
            ArchiveNamespace,
            string.Join(
                '\u001F',
                descriptor.SiteId,
                descriptor.EquipmentPath.Value,
                descriptor.UnitId ?? string.Empty,
                descriptor.CurveStartAt.ToString("O", CultureInfo.InvariantCulture),
                descriptor.CurveEndAt.ToString("O", CultureInfo.InvariantCulture),
                sha256));

    private static string CreateObjectKey(RawCurveDescriptor descriptor, string sha256) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{descriptor.SiteId}/{descriptor.CurveStartAt:yyyy/MM/dd}/{sha256}.csv");

    private static string RequireVersionId(string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId)
            ? versionId
            : throw new InvalidOperationException(
                "S3 did not return a version ID; the raw-curve bucket is not safely versioned.");

    private static string? MetadataValue(GetObjectMetadataResponse metadata, string key)
    {
        var fullKey = "x-amz-meta-" + key;
        return metadata.Metadata.Keys
            .FirstOrDefault(candidate =>
                string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, fullKey, StringComparison.OrdinalIgnoreCase)) is { } matched
            ? metadata.Metadata[matched]
            : null;
    }

    private static void EnsureSameIdentity(
        RawCurveArchiveEntry entry,
        RawCurveDescriptor descriptor,
        RawCurveDigest digest)
    {
        if (!string.Equals(entry.SiteId, descriptor.SiteId, StringComparison.Ordinal)
            || !string.Equals(entry.EquipmentId, descriptor.EquipmentPath.Value, StringComparison.Ordinal)
            || !string.Equals(entry.UnitId, descriptor.UnitId, StringComparison.Ordinal)
            || entry.CurveStartAt != descriptor.CurveStartAt
            || entry.CurveEndAt != descriptor.CurveEndAt
            || !string.Equals(entry.Sha256, digest.Hex, StringComparison.Ordinal)
            || entry.ByteSize != digest.ByteSize)
        {
            throw new InvalidOperationException(
                "The deterministic raw-curve archive ID resolved to different plant evidence.");
        }
    }

    private static RawCurveArchiveResult Result(
        RawCurveArchiveEntry entry,
        bool objectCreated,
        bool metadataCreated) =>
        new(
            entry.ArchiveId,
            entry.ObjectKey,
            entry.ObjectVersionId,
            entry.Sha256,
            entry.ByteSize,
            objectCreated,
            metadataCreated);

    private static void ValidateSiteId(string siteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        if (siteId.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("Site ID must contain only upper-case ASCII letters and digits.", nameof(siteId));
        }
    }

    private sealed record RawCurveArchiveEntry(
        Guid ArchiveId,
        string SiteId,
        string EquipmentId,
        string? UnitId,
        DateTimeOffset CurveStartAt,
        DateTimeOffset CurveEndAt,
        string ObjectKey,
        string ObjectVersionId,
        string Sha256,
        long ByteSize,
        DateTimeOffset RecordedAt,
        string Actor,
        string Reason,
        Guid? SupersedesArchiveId);
}
