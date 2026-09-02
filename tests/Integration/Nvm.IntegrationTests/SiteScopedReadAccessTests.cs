using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>K3 — client read-only vào một nhà máy vì server quyết định, không vì query.</summary>
/// <remarks>
/// <para>
/// Audit M3 phát hiện Grafana datasource giữ <c>SELECT</c> trên toàn schema <c>ts</c>, trong khi biến
/// <c>site</c> của dashboard là value do browser gửi. Đó là display filter, không phải authorization:
/// ai mở được panel editor cũng có thể gõ nhà máy khác. Migration 008 chuyển quyết định xuống database.
/// </para>
/// <para>
/// Các assertion này cố ý viết từ phía attacker. Chứng minh scoped view trả về một site chỉ chứng minh
/// happy path; điều phải đúng là <b>không có</b> query nào role viết được mà trả về site kia, và cách
/// duy nhất phát biểu điều đó là thử các query hiển nhiên rồi bị từ chối.
/// </para>
/// </remarks>
public sealed class SiteScopedReadAccessTests
{
    private const string Reader = "nvm_grafana_test";
    private const string ReaderPassword = "reader_integration_only";

    private static readonly EquipmentPath HaiPhongChannel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    private static readonly EquipmentPath LeipzigChannel =
        EquipmentPath.Parse("NOVAVOLT/DE1/MODULE/M1/MOD-01/MOD-01-ST-0001");

    [Fact]
    public async Task AGrantedRole_ReadsItsOwnPlantAndCannotReachAnother()
    {
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var owner = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await SeedBothPlantsAsync(owner);
        await ProvisionReaderAsync(owner, grantedSite: "NV1");

        await using var reader = NpgsqlDataSource.Create(ReaderConnectionString(postgres));

        // 1. Scoped view trả lời, và trả lời với một nhà máy. Cả hai nhà máy đều có row: filter trả về
        //    một site chỉ vì chỉ một site tồn tại thì không chứng minh được gì.
        (await TelemetryHypertableTests.ReadAsync(
            owner,
            "SELECT count(DISTINCT site_id)::text FROM ts.telemetry_measurement;"))
            .ShouldBe(["2"]);
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            """
            SELECT count(*)::text, count(DISTINCT site_id)::text, min(site_id)
            FROM ts_scoped.telemetry_measurement;
            """))
            .ShouldBe(["1", "1", "NV1"]);

        // 2. Hỏi nhà máy kia bằng tên trả về nothing thay vì row của người khác. Đây chính xác là thao
        //    tác mà biến site của dashboard từng cho phép.
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            "SELECT count(*)::text FROM ts_scoped.telemetry_measurement WHERE site_id = 'DE1';"))
            .ShouldBe(["0"]);

        // 3. Rollup cũng scoped. Nó là continuous aggregate chứ không phải table; fix chỉ cover raw
        //    hypertable sẽ để mọi panel dashboard đọc cái không scoped — nơi dashboard thực sự dùng
        //    phần lớn thời gian.
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            """
            SELECT count(*)::text, count(DISTINCT site_id)::text, min(site_id)
            FROM ts_scoped.process_signal_1m;
            """))
            .ShouldBe(["1", "1", "NV1"]);

        // 4. Migration 014 expose child rollup mà dashboard cấp máy dùng. Chứng minh fixture có cả hai
        //    nhà máy trước khi hỏi qua scoped view; child rỗng sẽ khiến cả query được phép và bị từ chối
        //    cùng trông đúng.
        (await TelemetryHypertableTests.ReadAsync(
            owner,
            "SELECT count(DISTINCT site_id)::text FROM ts.process_signal_machine_1m;"))
            .ShouldBe(["2"]);
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            """
            SELECT count(*)::text, count(DISTINCT site_id)::text, min(site_id)
            FROM ts_scoped.process_signal_machine_1m;
            """))
            .ShouldBe(["1", "1", "NV1"]);
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            "SELECT count(*)::text FROM ts_scoped.process_signal_machine_1m WHERE site_id = 'DE1';"))
            .ShouldBe(["0"]);

        // 5. Site picker chỉ đưa thứ connection được phép đọc, không phải mọi thứ đang tồn tại.
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            "SELECT count(*)::text, min(site_id) FROM ts_scoped.readable_site;"))
            .ShouldBe(["1", "NV1"]);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM ts.telemetry_measurement")]
    [InlineData("SELECT count(*) FROM ts.process_signal_1m")]
    [InlineData("SELECT count(*) FROM ts.process_signal_machine_1m")]
    [InlineData("SELECT count(*) FROM ts.process_signal")]
    [InlineData("SELECT count(*) FROM ts.site_read_grant")]
    public async Task TheReadingRole_IsRefusedEveryRouteAroundTheScopedViews(string sql)
    {
        // Base table, compatibility view, và chính grant table. Cái cuối quan trọng nhất: role đọc được
        // grant của mình thì biết nhà máy nào tồn tại; role ghi được nó thì không cần những cái khác.
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var owner = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await SeedBothPlantsAsync(owner);
        await ProvisionReaderAsync(owner, grantedSite: "NV1");

        await using var reader = NpgsqlDataSource.Create(ReaderConnectionString(postgres));

        var refusal = await Should.ThrowAsync<PostgresException>(
            async () => await TelemetryHypertableTests.ReadAsync(reader, sql + ";"));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task WideningTheGrant_IsAWriteTheReadingRoleCannotMake()
    {
        // Toàn bộ design dựa vào việc reader không sửa được authorization của mình. Viết thành test vì
        // "nó read-only" là claim về role attribute mà commit sau có thể đổi trong một dòng, và đây là
        // nơi sẽ phát hiện điều đó.
        await using var postgres = await TelemetryHypertableTests.StartAsync();
        IngestionSchemaMigrator.Upgrade(postgres.GetConnectionString());

        await using var owner = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await SeedBothPlantsAsync(owner);
        await ProvisionReaderAsync(owner, grantedSite: "NV1");

        await using var reader = NpgsqlDataSource.Create(ReaderConnectionString(postgres));

        var refusal = await Should.ThrowAsync<PostgresException>(
            async () => await TelemetryHypertableTests.ExecuteAsync(
                reader,
                "INSERT INTO ts.site_read_grant (role_name, site_id, reason) "
                + $"VALUES ('{Reader}', 'DE1', 'self-service');"));

        refusal.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        // Grant nhà máy theo đúng cách vẫn hoạt động, nên việc từ chối trên nói về người hỏi chứ không
        // phải cơ chế bị inert.
        await TelemetryHypertableTests.ExecuteAsync(
            owner,
            "INSERT INTO ts.site_read_grant (role_name, site_id, reason) "
            + $"VALUES ('{Reader}', 'DE1', 'granted by the owner in a test');");

        (await TelemetryHypertableTests.ReadAsync(
            reader,
            "SELECT count(*)::text FROM ts_scoped.telemetry_measurement;"))
            .ShouldBe(["2"]);
    }

    private static string ReaderConnectionString(Testcontainers.PostgreSql.PostgreSqlContainer postgres) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Username = Reader,
            Password = ReaderPassword,
        }.ConnectionString;

    private static async Task SeedBothPlantsAsync(NpgsqlDataSource owner)
    {
        var at = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        var ingestor = new PostgresMeasurementIngestor(
            owner,
            new FakeTimeProvider(at.AddMinutes(1)),
            new IngestionMetrics());

        await ingestor.IngestAsync(
            [Message("NV1", HaiPhongChannel, at), Message("DE1", LeipzigChannel, at)],
            CancellationToken.None);

        await TelemetryHypertableTests.ExecuteAsync(
            owner,
            "CALL refresh_continuous_aggregate('ts.process_signal_1m', NULL, NULL);");
        await TelemetryHypertableTests.ExecuteAsync(
            owner,
            "CALL refresh_continuous_aggregate('ts.process_signal_machine_1m', NULL, NULL);");
    }

    /// <summary>Các grant mà grafana-db-init của docker-compose tạo, dưới vai trò test.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification =
            "Both interpolated values are private constants of this test class; the statements are "
            + "role management, which cannot be expressed with query parameters.")]
    private static async Task ProvisionReaderAsync(NpgsqlDataSource owner, string grantedSite)
    {
        await TelemetryHypertableTests.ExecuteAsync(
            owner,
            $"""
             CREATE ROLE {Reader} LOGIN PASSWORD '{ReaderPassword}';
             ALTER ROLE {Reader} SET default_transaction_read_only = on;
             GRANT USAGE ON SCHEMA ts_scoped TO {Reader};
             GRANT SELECT ON ALL TABLES IN SCHEMA ts_scoped TO {Reader};
             INSERT INTO ts.site_read_grant (role_name, site_id, reason)
             VALUES ('{Reader}', '{grantedSite}', 'integration test');
             """);
    }

    private static DecodedSparkplugMessage Message(
        string siteId,
        EquipmentPath channel,
        DateTimeOffset deviceTimestamp)
    {
        var topic = SparkplugTopic.For(channel, SparkplugMessageType.DeviceData);
        var reading = new DeviceReading(
            "Formation/Voltage",
            Alias: 1,
            new MetricValue.Real(3.7),
            deviceTimestamp);

        return new DecodedSparkplugMessage(siteId, channel, topic, deviceTimestamp, [reading]);
    }
}
