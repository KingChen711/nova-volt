using System.Data;
using Nvm.CommandStore;
using Nvm.Contracts.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>Lot/cuộn có đang bị giữ không, đọc trong transaction của command tiêu hao (khoá chia sẻ tới commit).</summary>
public sealed class SqlMaterialHoldCheck(SqlCommandSession session) : IMaterialHoldCheck
{
    public async Task<string?> ActiveHoldAsync(string siteId, string lotKind, string lotId, decimal? spanFromMeter,
        decimal? spanToMeter, CancellationToken cancellationToken)
    {
        // Hold cả cuộn (không span) chặn mọi đoạn; hold một đoạn chỉ chặn đoạn giao với nó (nửa mở [from, to)).
        using var command = session.CreateCommand("""
            SELECT TOP (1) HoldId FROM quality.Holds WITH (HOLDLOCK)
            WHERE SiteId = @site AND Status = 'Active' AND TargetKind = @kind AND TargetId = @lot
              AND (SpanFromMeter IS NULL OR @from IS NULL OR (SpanFromMeter < @to AND @from < SpanToMeter))
            ORDER BY PlacedAt;
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = lotKind;
        command.Parameters.Add("@lot", SqlDbType.NVarChar, 100).Value = lotId;
        var from = command.Parameters.Add("@from", SqlDbType.Decimal);
        from.Precision = 12;
        from.Scale = 3;
        from.Value = (object?)spanFromMeter ?? DBNull.Value;
        var to = command.Parameters.Add("@to", SqlDbType.Decimal);
        to.Precision = 12;
        to.Scale = 3;
        to.Value = (object?)spanToMeter ?? DBNull.Value;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
