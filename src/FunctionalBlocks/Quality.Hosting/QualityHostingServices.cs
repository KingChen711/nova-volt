using System.Data;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.CommandStore;
using Nvm.Quality.Entities;
using Nvm.Quality.Handlers;
using Nvm.Quality.Ports;

namespace Nvm.Quality.Hosting;

/// <summary>Xác thực lại người ký bằng mật khẩu, ngay lúc ký (scope §13.3); không dùng phiên đang mở.</summary>
public interface IReauthenticator
{
    /// <summary>True nếu <paramref name="username"/> đăng nhập được bằng mật khẩu này và chính là <paramref name="subject"/>.</summary>
    Task<bool> VerifyAsync(string subject, string username, string password, CancellationToken cancellationToken);
}

/// <summary>Cấu hình client Keycloak dùng cho xác thực lại (direct grant chỉ bật trên client này).</summary>
public sealed record ReauthenticationOptions(Uri TokenEndpoint, string ClientId);

/// <summary>
/// Gọi token endpoint của Keycloak với grant <c>password</c> và so <c>sub</c> của token nhận được với người đang ký.
/// Mật khẩu chỉ đi thẳng tới IdP; không log, không lưu, không vào command.
/// </summary>
public sealed class KeycloakReauthenticator(HttpClient http, ReauthenticationOptions options) : IReauthenticator
{
    public async Task<bool> VerifyAsync(string subject, string username, string password, CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = options.ClientId,
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid",
        });
        using var response = await http.PostAsync(options.TokenEndpoint, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        { return false; }
        var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        if (!token.TryGetProperty("access_token", out var access) || access.GetString() is not { } jwt)
        { return false; }
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        { return false; }
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return claims.RootElement.TryGetProperty("sub", out var sub) &&
            string.Equals(sub.GetString(), subject, StringComparison.Ordinal);
    }
}

/// <summary>Đọc dữ liệu Quality ngoài transaction command, cho màn hình và phép kiểm chuỗi chữ ký.</summary>
public sealed class SqlQualityQueries(SqlCommandStoreOptions options)
{
    public sealed record HoldView(string HoldId, string TargetKind, string TargetId, decimal? SpanFromMeter,
        decimal? SpanToMeter, string ReasonCode, string? NcrId, string HeldBy, string Status, string ReleaseContentSha256,
        int CascadeTotal, int CascadeHeld, string? CascadeStatus);

    public async Task<HoldView?> HoldAsync(string siteId, string holdId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT h.TargetKind, h.TargetId, h.SpanFromMeter, h.SpanToMeter, h.ReasonCode, h.NcrId, h.HeldBy, h.Status,
                h.StreamVersion, coalesce(j.TotalUnits, 0),
                (SELECT count(*) FROM quality.HoldMembers m WHERE m.SiteId = h.SiteId AND m.HoldId = h.HoldId), j.Status
            FROM quality.Holds h LEFT JOIN quality.CascadeJobs j ON j.SiteId = h.SiteId AND j.JobId = h.HoldId
            WHERE h.SiteId = @site AND h.HoldId = @hold;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@hold", SqlDbType.VarChar, 64).Value = holdId;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        { return null; }
        decimal? from = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetDecimal(2);
        decimal? to = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetDecimal(3);
        var ncr = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5);
        var stored = new StoredHold(holdId, reader.GetString(0), reader.GetString(1), from, to, reader.GetString(4), ncr,
            reader.GetString(6), reader.GetString(7), reader.GetInt64(8));
        return new HoldView(holdId, stored.TargetKind, stored.TargetId, from, to, stored.ReasonCode, ncr, stored.HeldBy,
            stored.Status, stored.ReleaseContent, reader.GetInt32(9), reader.GetInt32(10),
            await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11));
    }

    public async Task<IReadOnlyList<SignatureRecord>> SignatureChainAsync(string siteId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT SignatureId, SubjectType, SubjectId, SignerId, SignerRole, Meaning, ContentSha256, SignedAt, PreviousHash, Hash
            FROM quality.Signatures WHERE SiteId = @site ORDER BY Sequence;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        var chain = new List<SignatureRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chain.Add(new SignatureRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                await reader.GetFieldValueAsync<DateTimeOffset>(7, cancellationToken).ConfigureAwait(false),
                reader.GetString(8), reader.GetString(9)));
        }
        return chain;
    }

    public async Task<IReadOnlyList<SpcSubgroup>> SpcAsync(string siteId, string characteristic, int last,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = new SqlCommand("""
            SELECT TOP (@last) SubgroupId, TakenAt, ValuesJson FROM quality.SpcSamples
            WHERE SiteId = @site AND Characteristic = @characteristic ORDER BY TakenAt DESC, SubgroupId DESC;
            """, connection);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@characteristic", SqlDbType.VarChar, 64).Value = characteristic;
        command.Parameters.Add("@last", SqlDbType.Int).Value = last;
        var groups = new List<SpcSubgroup>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(new SpcSubgroup(reader.GetString(0),
                await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false),
                JsonSerializer.Deserialize<decimal[]>(reader.GetString(2)) ?? []));
        }
        groups.Reverse();
        return groups;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

/// <summary>Subgroup SPC trên SQL, trong transaction của command.</summary>
public sealed class SqlSpcStore(SqlCommandSession session) : ISpcStore
{
    public async Task<bool> AddAsync(string siteId, string characteristic, SpcSubgroup subgroup, string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subgroup);
        using var command = session.CreateCommand("""
            IF EXISTS (SELECT 1 FROM quality.SpcSamples WITH (UPDLOCK, HOLDLOCK)
                       WHERE SiteId = @site AND Characteristic = @characteristic AND SubgroupId = @subgroup)
                SELECT 0;
            ELSE
            BEGIN
                INSERT INTO quality.SpcSamples (SiteId, Characteristic, SubgroupId, TakenAt, ValuesJson, RecordedBy)
                VALUES (@site, @characteristic, @subgroup, @at, @values, @actor);
                SELECT 1;
            END
            """);
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = siteId;
        command.Parameters.Add("@characteristic", SqlDbType.VarChar, 64).Value = characteristic;
        command.Parameters.Add("@subgroup", SqlDbType.VarChar, 64).Value = subgroup.SubgroupId;
        command.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = subgroup.TakenAt;
        command.Parameters.Add("@values", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(subgroup.Values);
        command.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = actorId;
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }
}
