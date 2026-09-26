using System.Data;
using Microsoft.Data.SqlClient;
using Nvm.ProductionExecution.Entities;

namespace Nvm.ProductionExecution.Hosting;

/// <summary>Kết quả một lượt đối chiếu saga formation/aging.</summary>
/// <param name="MissingTimeouts">Quá trình đang chờ nhưng không còn timeout nào sẽ đánh thức nó; đã lập lại.</param>
/// <param name="StreamsWithoutState">Stream <c>formation:*</c> có event nhưng mất dòng trạng thái: cần người xử lý.</param>
public sealed record FormationReconciliationReport(int MissingTimeouts, IReadOnlyList<string> StreamsWithoutState);

/// <summary>
/// Lab M7 #2: nếu trạng thái hay lịch timeout của saga bị mất, cell nằm im trong kho aging mãi mãi mà không
/// ai biết. Job này tìm hai loại "mồ côi" và lập lại timeout từ chính hạn đã lưu (không đoán hạn mới).
/// Chạy bằng credential migration/ops, không nằm trong luồng command.
/// </summary>
public static class FormationReconciliation
{
    public static async Task<FormationReconciliationReport> RunAsync(string connectionString, string siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using var restore = new SqlCommand("""
            INSERT INTO execution.ProcessTimeouts (SiteId, SerialNumber, Kind, DueAt)
            SELECT p.SiteId, p.SerialNumber, v.Kind, v.DueAt
            FROM execution.FormationAging p WITH (UPDLOCK, HOLDLOCK)
            CROSS APPLY (VALUES
                (CASE p.State WHEN 'Forming' THEN @formation WHEN 'Aging' THEN @aging END,
                 CASE p.State WHEN 'Forming' THEN p.FormationDueAt WHEN 'Aging' THEN p.AgingDueAt END)) v(Kind, DueAt)
            WHERE p.SiteId = @site AND p.State IN ('Forming', 'Aging')
              AND NOT EXISTS (SELECT 1 FROM execution.ProcessTimeouts t
                  WHERE t.SiteId = p.SiteId AND t.SerialNumber = p.SerialNumber AND t.Kind = v.Kind AND t.CompletedAt IS NULL);
            SELECT @@ROWCOUNT;
            """, connection, transaction);
        restore.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        restore.Parameters.Add("@formation", SqlDbType.VarChar, 30).Value = FormationAgingRules.FormationTimeoutKind;
        restore.Parameters.Add("@aging", SqlDbType.VarChar, 30).Value = FormationAgingRules.AgingDueKind;
        var missing = (int)(await restore.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        using var orphans = new SqlCommand("""
            SELECT s.StreamId FROM es.Streams s
            WHERE s.SiteId = @site AND s.StreamType = 'formation-aging'
              AND NOT EXISTS (SELECT 1 FROM execution.FormationAging p
                  WHERE p.SiteId = s.SiteId AND 'formation:' + p.SerialNumber = s.StreamId)
            ORDER BY s.StreamId;
            """, connection, transaction);
        orphans.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        var streams = new List<string>();
        using (var reader = await orphans.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            { streams.Add(reader.GetString(0)); }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FormationReconciliationReport(missing, streams);
    }
}
