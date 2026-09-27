using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Contracts.Ports;
using Nvm.MasterData.Entities;
using Nvm.MasterData.Handlers;

namespace Nvm.MasterData.Hosting;

/// <summary>Master data trên SQL, trong transaction của command đang giữ claim.</summary>
public sealed class SqlMasterDataStore(SqlCommandSession session) : IMasterDataStore
{
    public async Task<CanonicalItem?> ItemAsync(string siteId, string kind, string canonicalId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT Name, BaseUom FROM masterdata.Items WHERE SiteId = @site AND Kind = @kind AND CanonicalId = @id;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = kind;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = canonicalId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CanonicalItem(kind, canonicalId, reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1))
            : null;
    }

    public async Task AddItemAsync(string siteId, CanonicalItem item, string actorId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO masterdata.Items (SiteId, Kind, CanonicalId, Name, BaseUom, DefinedBy, DefinedAt)
            VALUES (@site, @kind, @id, @name, @uom, @actor, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = item.Kind;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = item.CanonicalId;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = item.Name;
        command.Parameters.Add("@uom", SqlDbType.NVarChar, 20).Value = (object?)item.BaseUom ?? DBNull.Value;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> AliasAsync(string siteId, string kind, string normalizedCode, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT CanonicalId FROM masterdata.IdentityAliases WHERE SiteId = @site AND Kind = @kind AND ExternalCode = @code;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = kind;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = normalizedCode;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task AddAliasAsync(string siteId, string kind, string normalizedCode, string canonicalId, string reason,
        string actorId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO masterdata.IdentityAliases (SiteId, Kind, ExternalCode, CanonicalId, Reason, MappedBy, MappedAt)
            VALUES (@site, @kind, @code, @id, @reason, @actor, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = kind;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = normalizedCode;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = canonicalId;
        command.Parameters.Add("@reason", SqlDbType.NVarChar, 500).Value = reason;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> RevisionAsync(string siteId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("SELECT Revision FROM masterdata.Revisions WHERE SiteId = @site;");
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision ? revision : 0;
    }

    public async Task<long> BumpRevisionAsync(string siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE masterdata.Revisions WITH (UPDLOCK, HOLDLOCK) SET Revision = Revision + 1, UpdatedAt = @at
            OUTPUT inserted.Revision WHERE SiteId = @site;
            IF @@ROWCOUNT = 0
                INSERT INTO masterdata.Revisions (SiteId, Revision, UpdatedAt) OUTPUT inserted.Revision VALUES (@site, 1, @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            { return reader.GetInt64(0); }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        throw new InvalidOperationException("Master data revision was not written.");
    }

    public async Task<ReconciliationTask?> TaskForUpdateAsync(string siteId, string taskId, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT IssueKind, ExternalCode, CanonicalId, ReceivedUom, ExpectedUom, SourceDocument, Detail, Status, Resolution
            FROM masterdata.ReconciliationTasks WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND TaskId = @id;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 20).Value = taskId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        return await ReadTaskAsync(taskId, reader, 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> OpenTaskIdsForCodeAsync(string siteId, string issueKind, string normalizedCode,
        CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            SELECT TaskId FROM masterdata.ReconciliationTasks WITH (UPDLOCK, HOLDLOCK)
            WHERE SiteId = @site AND Status = 'Open' AND IssueKind = @kind AND ExternalCode = @code ORDER BY TaskId;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 20).Value = issueKind;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = normalizedCode;
        var ids = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { ids.Add(reader.GetString(0)); }
        return ids;
    }

    public async Task AddTaskAsync(string siteId, string taskId, ReconciliationIssue issue, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        Require(siteId);
        using var command = session.CreateCommand("""
            INSERT INTO masterdata.ReconciliationTasks (SiteId, TaskId, IssueKind, ExternalCode, CanonicalId, ReceivedUom,
                ExpectedUom, SourceDocument, Detail, Status, OpenedAt)
            VALUES (@site, @id, @kind, @code, @canonical, @received, @expected, @document, @detail, 'Open', @at);
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 20).Value = taskId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 20).Value = issue.Kind;
        command.Parameters.Add("@code", SqlDbType.NVarChar, 100).Value = IdentityCode.Normalize(issue.ExternalCode);
        command.Parameters.Add("@canonical", SqlDbType.NVarChar, 100).Value = (object?)issue.CanonicalId ?? DBNull.Value;
        command.Parameters.Add("@received", SqlDbType.NVarChar, 20).Value = (object?)issue.ReceivedUom ?? DBNull.Value;
        command.Parameters.Add("@expected", SqlDbType.NVarChar, 20).Value = (object?)issue.ExpectedUom ?? DBNull.Value;
        command.Parameters.Add("@document", SqlDbType.NVarChar, 200).Value = issue.SourceDocument;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 1000).Value = issue.Detail;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveTaskAsync(string siteId, string taskId, string resolution, string note, string actorId,
        DateTimeOffset at, CancellationToken cancellationToken)
    {
        Require(siteId);
        using var command = session.CreateCommand("""
            UPDATE masterdata.ReconciliationTasks SET Status = 'Resolved', Resolution = @resolution, Note = @note,
                ResolvedBy = @actor, ResolvedAt = @at
            WHERE SiteId = @site AND TaskId = @id AND Status = 'Open';
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@id", SqlDbType.VarChar, 20).Value = taskId;
        command.Parameters.Add("@resolution", SqlDbType.VarChar, 10).Value = resolution;
        command.Parameters.Add("@note", SqlDbType.NVarChar, 500).Value = note;
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = at;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        { throw new InvalidOperationException($"Reconciliation task {taskId} was not open."); }
    }

    internal static async Task<ReconciliationTask> ReadTaskAsync(string taskId, SqlDataReader reader, int offset,
        CancellationToken cancellationToken)
    {
        async Task<string?> Optional(int index) =>
            await reader.IsDBNullAsync(offset + index, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(offset + index);
        return new ReconciliationTask(taskId, new ReconciliationIssue(reader.GetString(offset), reader.GetString(offset + 1),
                await Optional(2).ConfigureAwait(false), await Optional(3).ConfigureAwait(false),
                await Optional(4).ConfigureAwait(false), reader.GetString(offset + 5), reader.GetString(offset + 6)),
            reader.GetString(offset + 7), await Optional(8).ConfigureAwait(false));
    }

    private void Require(string siteId)
    {
        if (!string.Equals(session.SiteId, siteId, StringComparison.Ordinal))
        { throw new InvalidOperationException("Master data site does not match the active command transaction."); }
    }
}
