using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Quality.Entities;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>Hold, cascade, chữ ký và NCR trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlQualityStores(SqlCommandSession session) : IHoldStore, ISignatureStore, INcrStore
{
    public async Task<StoredHold?> LoadForUpdateAsync(string siteId, string holdId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TargetKind, TargetId, SpanFromMeter, SpanToMeter, ReasonCode, NcrId, HeldBy, Status, StreamVersion
            FROM quality.Holds WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND HoldId = @hold;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = holdId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return new StoredHold(holdId, reader.GetString(0), reader.GetString(1),
            await NullableAsync<decimal>(reader, 2, cancellationToken).ConfigureAwait(false),
            await NullableAsync<decimal>(reader, 3, cancellationToken).ConfigureAwait(false),
            reader.GetString(4), await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt64(8));
    }

    public async Task CreateAsync(string siteId, StoredHold hold, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hold);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO quality.Holds (SiteId, HoldId, TargetKind, TargetId, SpanFromMeter, SpanToMeter, ReasonCode, NcrId,
                HeldBy, Status, StreamVersion, PlacedAt)
            VALUES (@site, @hold, @kind, @target, @from, @to, @reason, @ncr, @by, 'Active', @version, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = hold.HoldId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = hold.TargetKind;
        command.Parameters.Add("@target", SqlDbType.NVarChar, 100).Value = hold.TargetId;
        AddDecimal(command, "@from", hold.SpanFromMeter);
        AddDecimal(command, "@to", hold.SpanToMeter);
        command.Parameters.Add("@reason", SqlDbType.VarChar, 64).Value = hold.ReasonCode;
        command.Parameters.Add("@ncr", SqlDbType.VarChar, 64).Value = (object?)hold.NcrId ?? DBNull.Value;
        command.Parameters.Add("@by", SqlDbType.NVarChar, 200).Value = hold.HeldBy;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = hold.StreamVersion;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetReleasedAsync(string siteId, string holdId, long streamVersion, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE quality.Holds SET Status = 'Released', StreamVersion = @version, ReleasedAt = @at
            WHERE SiteId = @site AND HoldId = @hold AND Status = 'Active';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = holdId;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = streamVersion;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("Hold changed concurrently."); }
    }

    public async Task<CascadeProgress?> LoadJobForUpdateAsync(string siteId, string jobId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT HoldId, Status, TotalUnits, NextChunk, ChunkSize, StreamVersion FROM quality.CascadeJobs WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND JobId = @job;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CascadeProgress(jobId, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt64(5))
            : null;
    }

    public async Task<int> AddTargetsAsync(string siteId, string jobId, string holdId, IReadOnlyList<string> serials,
        int chunkSize, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serials);
        Require(siteId);
        using (var ensure = session.CreateCommand("""
            IF NOT EXISTS (SELECT 1 FROM quality.CascadeJobs WHERE SiteId = @site AND JobId = @job)
                INSERT INTO quality.CascadeJobs (SiteId, JobId, HoldId, Status, TotalUnits, NextChunk, ChunkSize, PlanRounds, UpdatedAt)
                VALUES (@site, @job, @hold, 'Running', 0, 0, @chunk, 0, @at);
            """))
        {
            ensure.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            ensure.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
            ensure.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = holdId;
            ensure.Parameters.Add("@chunk", SqlDbType.Int).Value = chunkSize;
            ensure.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // Không tham số: batch chạy ở phạm vi session, nên bảng tạm còn sống cho bulk copy phía sau
        // (tạo trong sp_executesql thì bảng tạm mất ngay khi lệnh đó kết thúc).
        using (var temp = session.CreateCommand(
            "CREATE TABLE #targets (SerialNumber varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);"))
        { await temp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        var table = new DataTable();
        table.Columns.Add("SerialNumber", typeof(string));
        foreach (var serial in serials.Distinct(StringComparer.Ordinal))
        { table.Rows.Add(serial); }
        using (var copy = session.CreateBulkCopy("#targets"))
        { await copy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false); }
        // Mục tiêu mới nối vào cuối theo thứ tự serial; mục tiêu đã có giữ nguyên Ordinal (checkpoint vẫn đúng).
        using var append = session.CreateCommand("""
            DECLARE @start int = (SELECT coalesce(max(Ordinal), -1) + 1 FROM quality.CascadeTargets
                                  WHERE SiteId = @site AND JobId = @job);
            INSERT INTO quality.CascadeTargets (SiteId, JobId, Ordinal, SerialNumber)
            SELECT @site, @job, @start + ROW_NUMBER() OVER (ORDER BY t.SerialNumber) - 1, t.SerialNumber
            FROM #targets t
            WHERE NOT EXISTS (SELECT 1 FROM quality.CascadeTargets c
                              WHERE c.SiteId = @site AND c.JobId = @job AND c.SerialNumber = t.SerialNumber);
            DROP TABLE #targets;
            DECLARE @total int = (SELECT count(*) FROM quality.CascadeTargets WHERE SiteId = @site AND JobId = @job);
            UPDATE quality.CascadeJobs SET TotalUnits = @total, UpdatedAt = @at, PlanRounds = PlanRounds + 1,
                Status = CASE WHEN NextChunk * ChunkSize >= @total AND @total > 0 THEN 'Completed' ELSE 'Running' END
            WHERE SiteId = @site AND JobId = @job;
            SELECT @total;
            """);
        append.CommandTimeout = 300;
        append.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        append.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
        append.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        return (int)(await append.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<string>> ReadChunkAsync(string siteId, string jobId, int chunkIndex, int chunkSize,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT SerialNumber FROM quality.CascadeTargets
            WHERE SiteId = @site AND JobId = @job AND Ordinal >= @from AND Ordinal < @to ORDER BY Ordinal;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
        command.Parameters.Add("@from", SqlDbType.Int).Value = chunkIndex * chunkSize;
        command.Parameters.Add("@to", SqlDbType.Int).Value = (chunkIndex + 1) * chunkSize;
        var serials = new List<string>(chunkSize);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { serials.Add(reader.GetString(0)); }
        return serials;
    }

    public async Task SetStreamVersionAsync(string siteId, string jobId, long streamVersion, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand(
            "UPDATE quality.CascadeJobs SET StreamVersion = @version WHERE SiteId = @site AND JobId = @job;");
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = streamVersion;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyChunkAsync(string siteId, string jobId, string holdId, int chunkIndex, int chunkSize,
        IReadOnlyList<string> serials, bool completed, long streamVersion, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        // Set-based: một câu cho cả chunk. Thành viên đã có (chạy lại) bị bỏ qua.
        using var command = session.CreateCommand("""
            INSERT INTO quality.HoldMembers (SiteId, SerialNumber, HoldId, HeldAt)
            SELECT @site, t.SerialNumber, @hold, @at FROM quality.CascadeTargets t
            WHERE t.SiteId = @site AND t.JobId = @job AND t.Ordinal >= @from AND t.Ordinal < @to
              AND NOT EXISTS (SELECT 1 FROM quality.HoldMembers m
                              WHERE m.SiteId = @site AND m.SerialNumber = t.SerialNumber AND m.HoldId = @hold);
            UPDATE quality.CascadeJobs SET NextChunk = @next, UpdatedAt = @at, StreamVersion = @version,
                Status = CASE WHEN @completed = 1 THEN 'Completed' ELSE 'Running' END
            WHERE SiteId = @site AND JobId = @job AND NextChunk = @chunk;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@job", SqlDbType.VarChar, 64).Value = jobId;
        command.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = holdId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        command.Parameters.Add("@chunk", SqlDbType.Int).Value = chunkIndex;
        command.Parameters.Add("@next", SqlDbType.Int).Value = chunkIndex + 1;
        command.Parameters.Add("@from", SqlDbType.Int).Value = chunkIndex * chunkSize;
        command.Parameters.Add("@to", SqlDbType.Int).Value = chunkIndex * chunkSize + serials.Count;
        command.Parameters.Add("@completed", SqlDbType.Bit).Value = completed;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = streamVersion;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> HeadForUpdateAsync(string siteId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TOP (1) Hash FROM quality.Signatures WITH (UPDLOCK, HOLDLOCK, TABLOCKX)
            WHERE SiteId = @site ORDER BY Sequence DESC;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? SignatureChain.Genesis;
    }

    public async Task AppendAsync(string siteId, SignatureRecord signature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signature);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO quality.Signatures (SiteId, Sequence, SignatureId, SubjectType, SubjectId, SignerId, SignerRole,
                Meaning, ContentSha256, SignedAt, PreviousHash, Hash)
            SELECT @site, coalesce(max(Sequence), 0) + 1, @id, @type, @subject, @signer, @role, @meaning, @content, @at,
                @previous, @hash
            FROM quality.Signatures WHERE SiteId = @site;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = signature.SignatureId;
        command.Parameters.Add("@type", SqlDbType.VarChar, 30).Value = signature.SubjectType;
        command.Parameters.Add("@subject", SqlDbType.NVarChar, 100).Value = signature.SubjectId;
        command.Parameters.Add("@signer", SqlDbType.NVarChar, 200).Value = signature.SignerId;
        command.Parameters.Add("@role", SqlDbType.VarChar, 50).Value = signature.SignerRole;
        command.Parameters.Add("@meaning", SqlDbType.VarChar, 20).Value = signature.Meaning;
        command.Parameters.Add("@content", SqlDbType.Char, 64).Value = signature.ContentSha256;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = signature.SignedAt;
        command.Parameters.Add("@previous", SqlDbType.Char, 64).Value = signature.PreviousHash;
        command.Parameters.Add("@hash", SqlDbType.Char, 64).Value = signature.Hash;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SignatureRecord>> LoadAsync(string siteId, IReadOnlyCollection<string> signatureIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signatureIds);
        Require(siteId);
        var result = new List<SignatureRecord>();
        foreach (var id in signatureIds.Distinct(StringComparer.Ordinal))
        {
            using var command = session.CreateCommand(SelectSignatures + " AND SignatureId = @id;");
            command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
            command.Parameters.Add("@id", SqlDbType.VarChar, 64).Value = id;
            result.AddRange(await ReadSignaturesAsync(command, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    public async Task<IReadOnlyList<SignatureRecord>> ChainAsync(string siteId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand(SelectSignatures + " ORDER BY Sequence;");
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        return await ReadSignaturesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredNcr?> LoadNcrForUpdateAsync(string siteId, string ncrId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT SerialNumber, ReasonCode, Description, RaisedBy, Status, StreamVersion
            FROM quality.NonConformance WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND NcrId = @ncr;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@ncr", SqlDbType.VarChar, 64).Value = ncrId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredNcr(ncrId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5))
            : null;
    }

    public async Task CloseAsync(string siteId, string ncrId, string disposition, long streamVersion, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE quality.NonConformance SET Status = 'Closed', StreamVersion = @version
            WHERE SiteId = @site AND NcrId = @ncr AND Status <> 'Closed';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@ncr", SqlDbType.VarChar, 64).Value = ncrId;
        command.Parameters.Add("@version", SqlDbType.BigInt).Value = streamVersion;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException("NCR changed concurrently."); }
    }

    private const string SelectSignatures = """
        SELECT SignatureId, SubjectType, SubjectId, SignerId, SignerRole, Meaning, ContentSha256, SignedAt, PreviousHash, Hash
        FROM quality.Signatures WHERE SiteId = @site
        """;

    private static async Task<List<SignatureRecord>> ReadSignaturesAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<SignatureRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SignatureRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
                reader.GetString(8), reader.GetString(9)));
        }
        return result;
    }

    private static async Task<T?> NullableAsync<T>(SqlDataReader reader, int ordinal, CancellationToken cancellationToken)
        where T : struct =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null : await reader.GetFieldValueAsync<T>(ordinal, cancellationToken).ConfigureAwait(false);

    private static void AddDecimal(SqlCommand command, string name, decimal? value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 12;
        parameter.Scale = 3;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Quality site does not match the active command transaction."); }
    }
}
