using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nvm.Ingestion;
using Nvm.Ingestion.Persistence;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;
using Nvm.Sparkplug.Topics;

namespace Nvm.IntegrationTests;

/// <summary>K3 — a read-only client reaches one plant because the server says so, not the query.</summary>
/// <remarks>
/// <para>
/// The M3 audit found the Grafana datasource holding <c>SELECT</c> on the whole <c>ts</c> schema while
/// the dashboard's <c>site</c> variable was a value the browser sent. That is a display filter, and a
/// display filter is not authorization: anyone who could open a panel editor could type a different
/// plant. Migration 008 moved the decision to the database.
/// </para>
/// <para>
/// These assertions are written from the attacker's side on purpose. Proving the scoped view returns
/// one site proves the happy path; what has to hold is that there is <b>no</b> query the role can
/// write that returns the other one, and the only way to state that is to try the obvious ones and be
/// refused.
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

        // 1. The scoped view answers, and it answers with one plant. Both plants have rows: a filter
        //    that returned one site because only one site existed would prove nothing at all.
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

        // 2. Asking for the other plant by name returns nothing rather than someone else's rows. This
        //    is the exact move the dashboard's site variable used to make possible.
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            "SELECT count(*)::text FROM ts_scoped.telemetry_measurement WHERE site_id = 'DE1';"))
            .ShouldBe(["0"]);

        // 3. The rollup is scoped too. It is a continuous aggregate rather than a table, and a fix
        //    that covered only the raw hypertable would leave every dashboard panel reading the
        //    unscoped one — which is where the dashboard actually spends most of its time.
        (await TelemetryHypertableTests.ReadAsync(
            reader,
            """
            SELECT count(*)::text, count(DISTINCT site_id)::text, min(site_id)
            FROM ts_scoped.process_signal_1m;
            """))
            .ShouldBe(["1", "1", "NV1"]);

        // 4. Migration 014 exposes the child rollup used by the machine-level dashboard. Prove the
        //    fixture contains both plants before asking through the scoped view; an empty child would
        //    otherwise make both the allowed and denied query look correct.
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

        // 5. The site picker offers what the connection may read, not what exists.
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
        // Base tables, the compatibility view, and the grant table itself. The last one matters most:
        // a role that could read its own grant knows which plants exist, and a role that could write
        // it would not need any of the others.
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
        // The whole design rests on the reader not being able to edit its own authorization. Stated
        // as a test because "it is read-only" is a claim about a role attribute that a later commit
        // can change in one line, and this is where that would be noticed.
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

        // And granting the plant the proper way does work, so the refusal above is about who asked
        // rather than about the mechanism being inert.
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

    /// <summary>The grants docker-compose's grafana-db-init makes, as a test role.</summary>
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
