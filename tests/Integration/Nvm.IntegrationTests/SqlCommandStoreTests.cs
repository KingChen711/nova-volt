using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nvm.App.Execution;
using Nvm.CommandStore;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;
using Testcontainers.MsSql;

namespace Nvm.IntegrationTests;

/// <summary>
/// C05: claim, business effect và outcome trong CÙNG một SQL Server transaction. Dùng SQL Server thật
/// qua Testcontainers (image pin ở ADR-001/plan M4), không mock, không InMemory. Mỗi "scope" trong test
/// dựng riêng <see cref="SqlCommandSession"/> + <see cref="SqlIdempotencyStore"/> đúng như DI scoped mỗi
/// request, và chạy qua <see cref="IdempotencyBehavior{TCommand, TResult}"/> thật để kiểm cả việc Complete
/// nằm trong cùng try với handler (rollback khi commit lỗi).
/// </summary>
public sealed class SqlCommandStoreTests : IClassFixture<SqlCommandStoreFixture>
{
    private readonly SqlCommandStoreFixture _fixture;

    public SqlCommandStoreTests(SqlCommandStoreFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptedSubmission_CommitsEffectClaimAndOutcomeTogether_AndReplays()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-ACCEPT-1", "{\"value\":401.25}");
        var handlerCalls = 0;

        var first = await _fixture.SubmitAsync(command, async session =>
        {
            Interlocked.Increment(ref handlerCalls);
            await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        first.Accepted.ShouldBeTrue();
        handlerCalls.ShouldBe(1);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);

        // Replay trong một scope mới: outcome cũ trả lại, handler KHÔNG chạy lại, không thêm effect.
        var replay = await _fixture.SubmitAsync(command, session =>
        {
            Interlocked.Increment(ref handlerCalls);
            return Task.FromResult(new CollectionOutcome(true, "SHOULD-NOT-RUN"));
        }, cancellationToken: Ct);

        replay.ShouldBe(first);
        handlerCalls.ShouldBe(1);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task RejectedSubmission_CommitsOutcomeWithoutEffect_AndReplaysTheSameRejection()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-REJECT-1", "{\"value\":1}");

        var rejected = await _fixture.SubmitAsync(command, session =>
            // Rejection nghiệp vụ: KHÔNG ghi effect, nhưng outcome vẫn commit để replay trả cùng lý do.
            Task.FromResult(new CollectionOutcome(false, "PACK_HELD")), cancellationToken: Ct);

        rejected.Accepted.ShouldBeFalse();
        rejected.ReasonCode.ShouldBe("PACK_HELD");
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(0);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);

        var replay = await _fixture.SubmitAsync(command, session =>
            Task.FromResult(new CollectionOutcome(true, "SHOULD-NOT-RUN")), cancellationToken: Ct);
        replay.ShouldBe(rejected);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task HandlerThrows_RollsBackEffectAndClaim_AndAllowsRetry()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-THROW-1", "{\"value\":2}");

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _fixture.SubmitAsync(command, async session =>
            {
                await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
                throw new InvalidOperationException("handler boom");
            }, cancellationToken: Ct));

        // Handler ném → Abandon → DisposeAsync rollback: không effect, không claim.
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(0);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(0);

        // Cùng khoá gửi lại phải chạy được và chỉ tạo đúng một effect.
        var retry = await _fixture.SubmitAsync(command, async session =>
        {
            await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);
        retry.Accepted.ShouldBeTrue();
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task OutcomeWriteFails_RollsBackEffectAndClaim_ThenSameScopeCanRetry()
    {
        await _fixture.ExecuteAsync("""
            ALTER TABLE command_store.CommandOutcomes ADD CONSTRAINT CK_TestOutcomeFailure
            CHECK (OutcomeJson NOT LIKE '%FAIL-OUTCOME%');
            """, _ => { }, Ct);
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-OUTCOME-FAIL", "payload");
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, _fixture.Options(), _fixture.SharedInMemory);
        var behavior = new IdempotencyBehavior<RecordDataCollection, CollectionOutcome>(store, TimeProvider.System);
        await Should.ThrowAsync<SqlException>(() => behavior.HandleAsync(command, async () =>
        {
            await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
            return new CollectionOutcome(true, "FAIL-OUTCOME");
        }, Ct));
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(0);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(0);
        await behavior.HandleAsync(command, async () =>
        {
            await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
            return new CollectionOutcome(true, "OK");
        }, Ct);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task HeldClaimCancellation_DoesNotAbandonOtherOwnersClaim()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "cancel-wait", "payload");
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, _fixture.Options(), _fixture.SharedInMemory);
        store.Prepare(command);
        await store.ClaimAsync<CollectionOutcome>(command.IdempotencyKey, nameof(RecordDataCollection), Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Should.ThrowAsync<OperationCanceledException>(() => _fixture.SubmitAsync(command,
            _ => throw new InvalidOperationException("Must not run"), cancellationToken: timeout.Token));
        await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
        await store.CompleteAsync(command.IdempotencyKey, new CollectionOutcome(true, "OWNER"), TimeProvider.System.GetUtcNow(), Ct);
        (await _fixture.SubmitAsync(command, _ => throw new InvalidOperationException("Must replay"), cancellationToken: Ct))
            .ReasonCode.ShouldBe("OWNER");
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task AbandonAfterEffectButBeforeCommit_LeavesNothingPersisted()
    {
        // Mô phỏng thất bại SAU khi ghi effect nhưng TRƯỚC khi commit (ví dụ commit/outcome lỗi, hoặc
        // process chết): holder gọi Abandon → transaction rollback, không effect, không claim.
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-ABANDON-1", "{\"value\":3}");
        var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, _fixture.Options(), _fixture.SharedInMemory);
        store.Prepare(command);
        var claim = await store.ClaimAsync<CollectionOutcome>(command.IdempotencyKey, nameof(RecordDataCollection), Ct);
        claim.IsGranted.ShouldBeTrue();
        await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
        await store.AbandonAsync(command.IdempotencyKey, Ct);

        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(0);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Replay_InFreshStore_ReturnsOriginalOutcome_WithoutRerunningOrDeletingTheClaim()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-RESTART-1", "{\"value\":4}");
        var original = await _fixture.SubmitAsync(command, async session =>
        {
            await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
            return new CollectionOutcome(true, "COMMITTED");
        }, cancellationToken: Ct);

        // "Restart" = scope/connection/store hoàn toàn mới, cùng DB (fixture giữ nguyên container).
        var handlerRan = false;
        var afterRestart = await _fixture.SubmitAsync(command, session =>
        {
            handlerRan = true;
            return Task.FromResult(new CollectionOutcome(true, "SHOULD-NOT-RUN"));
        }, cancellationToken: Ct);

        handlerRan.ShouldBeFalse();
        afterRestart.ShouldBe(original);
        // Committed claim vẫn còn (không bị Abandon/DELETE khi mất response) và số effect không đổi.
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task InMemoryStore_CannotReplayAfterRestart_ReproducingTheGapC05Closes()
    {
        // Chứng minh RED tái lập được: chỉ dùng production code có TRƯỚC C05 (InMemoryIdempotencyStore),
        // nên test này chạy y hệt trên parent. "Restart" = một instance store mới → mọi claim mất sạch,
        // nên cùng khoá được CẤP LẠI (handler sẽ chạy lần hai) thay vì replay. Đó chính là lỗ hổng mà
        // store SQL bền vững đóng lại ở test restart phía trên.
        var key = IdempotencyKey.FromNaturalKey("NV1", "AnyCommand", "SUB-RAM-RESTART");
        var before = new InMemoryIdempotencyStore(TimeProvider.System);
        var granted = await before.ClaimAsync<int>(key, "AnyCommand", Ct);
        granted.IsGranted.ShouldBeTrue();
        await before.CompleteAsync(key, 42, TimeProvider.System.GetUtcNow(), Ct);
        (await before.ClaimAsync<int>(key, "AnyCommand", Ct)).IsGranted.ShouldBeFalse(); // cùng process: replay

        var afterRestart = new InMemoryIdempotencyStore(TimeProvider.System);
        var reclaim = await afterRestart.ClaimAsync<int>(key, "AnyCommand", Ct);
        reclaim.IsGranted.ShouldBeTrue(); // mất trí nhớ qua "restart" → chạy lại. Đây là bug C05 sửa.
    }

    [Fact]
    public async Task TwoScopesRacingTheSameKey_ProduceExactlyOneEffect_AndBothSeeTheSameOutcome()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-RACE-1", "{\"value\":5}");
        var handlerCalls = 0;

        async Task<CollectionOutcome> Contend()
        {
            return await _fixture.SubmitAsync(command, async session =>
            {
                Interlocked.Increment(ref handlerCalls);
                await SqlCommandStoreFixture.InsertEffectAsync(session, command, Ct);
                // Giữ handler một nhịp để hai scope thật sự chồng nhau.
                await Task.Delay(50, Ct);
                return new CollectionOutcome(true, "RACE");
            }, cancellationToken: Ct);
        }

        var results = await Task.WhenAll(Contend(), Contend());

        handlerCalls.ShouldBe(1); // range lock (UPDLOCK/HOLDLOCK) serialize; chỉ một scope chạy handler.
        results[0].ShouldBe(results[1]);
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task SameKeyWithDifferentActor_IsRejectedAsConflict_WithoutLeakingTheOtherOutcome()
    {
        var first = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CONFLICT-1", "{\"value\":6}");
        await _fixture.SubmitAsync(first, session => Task.FromResult(new CollectionOutcome(true, "FIRST")), cancellationToken: Ct);

        // Cùng site + submissionId (⇒ cùng khoá) nhưng actor khác: conflict, không trả outcome người khác.
        var otherActor = SqlCommandStoreFixture.NewCommand("NV1", "op.other", "SUB-CONFLICT-1", "{\"value\":6}");
        otherActor.IdempotencyKey.ShouldBe(first.IdempotencyKey);
        var conflict = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _fixture.SubmitAsync(otherActor, session => Task.FromResult(new CollectionOutcome(true, "X")), cancellationToken: Ct));
        conflict.Message.ShouldContain("conflict");
    }

    [Fact]
    public async Task SameKeyWithDifferentPayload_IsRejectedAsConflict()
    {
        var first = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CONFLICT-2", "{\"value\":7}");
        await _fixture.SubmitAsync(first, session => Task.FromResult(new CollectionOutcome(true, "FIRST")), cancellationToken: Ct);

        var otherPayload = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CONFLICT-2", "{\"value\":999}");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _fixture.SubmitAsync(otherPayload, session => Task.FromResult(new CollectionOutcome(true, "X")), cancellationToken: Ct));
    }

    [Fact]
    public async Task RenamedClrCommand_ReplaysUsingStableContractType()
    {
        var original = SqlCommandStoreFixture.NewCommand("NV1", "actor", "stable-type", "payload");
        await _fixture.SubmitAsync(original, _ => Task.FromResult(new CollectionOutcome(true, "ORIGINAL")), cancellationToken: Ct);
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, _fixture.Options(), _fixture.SharedInMemory);
        var behavior = new IdempotencyBehavior<RenamedSubmission, CollectionOutcome>(store, TimeProvider.System);
        var result = await behavior.HandleAsync(new RenamedSubmission(original),
            () => throw new InvalidOperationException("Must replay"), Ct);
        result.ReasonCode.ShouldBe("ORIGINAL");
    }

    [Fact]
    public async Task SameKeyWithDifferentResultType_IsRejectedAsConflict()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CONFLICT-3", "{\"value\":8}");
        await _fixture.SubmitAsync(command, session => Task.FromResult(new CollectionOutcome(true, "FIRST")), cancellationToken: Ct);

        // Cùng khoá nhưng đọc lại với TResult khác (int) → mismatch loại kết quả → conflict.
        var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, _fixture.Options(), _fixture.SharedInMemory);
        store.Prepare(command);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await store.ClaimAsync<int>(command.IdempotencyKey, nameof(RecordDataCollection), Ct));
    }

    [Fact]
    public async Task MismatchedKey_IsRejectedBeforeTouchingTheDatabase()
    {
        // Khoá không suy ra từ (site, commandType, submissionId) của chính nó → ArgumentException.
        var bogus = new RecordDataCollection("NV1", "op.nv1", "SUB-KEYBAD", "{\"value\":9}",
            IdempotencyKey.FromNaturalKey("NV1", nameof(RecordDataCollection), "A-DIFFERENT-SUBMISSION"));
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _fixture.SubmitAsync(bogus, session => Task.FromResult(new CollectionOutcome(true, "X")), cancellationToken: Ct));
    }

    [Fact]
    public async Task CancellationBeforeClaimCompletes_CleansUpAndAllowsAFreshRetry()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CANCEL-1", "{\"value\":10}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await _fixture.SubmitAsync(command, session => Task.FromResult(new CollectionOutcome(true, "X")),
                cancellationToken: cts.Token));

        // Không có claim/effect treo lại; retry với token sạch thành công.
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(0);
        var retry = await _fixture.SubmitAsync(command, session => Task.FromResult(new CollectionOutcome(true, "OK")), cancellationToken: Ct);
        retry.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task ContendingAHeldClaim_TimesOutWithinTheBoundedWait()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-TIMEOUT-1", "{\"value\":11}");

        // Scope A giữ claim (đã insert, chưa commit) → giữ range lock.
        var sessionA = new SqlCommandSession();
        var storeA = new SqlIdempotencyStore(sessionA, _fixture.Options(), _fixture.SharedInMemory);
        storeA.Prepare(command);
        (await storeA.ClaimAsync<CollectionOutcome>(command.IdempotencyKey, nameof(RecordDataCollection), Ct))
            .IsGranted.ShouldBeTrue();
        try
        {
            // Scope B với timeout ngắn phải bị chặn rồi timeout, không chờ vô hạn.
            var sessionB = new SqlCommandSession();
            var storeB = new SqlIdempotencyStore(sessionB, _fixture.Options(timeoutSeconds: 2), _fixture.SharedInMemory);
            storeB.Prepare(command);
            var timeout = await Should.ThrowAsync<SqlException>(async () =>
                await storeB.ClaimAsync<CollectionOutcome>(command.IdempotencyKey, nameof(RecordDataCollection), Ct));
            timeout.Message.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            await storeA.AbandonAsync(command.IdempotencyKey, Ct); // giải phóng lock
        }
    }

    [Fact]
    public async Task SeededContext_MatchesTheFixture_IncludingUnitKind_AndIsSiteScoped()
    {
        // Context đọc TRONG transaction của command, site ép từ session. Kiểm khớp OperatorFixture,
        // đặc biệt UnitKind (bug Kind→UnitKind đã sửa), và cách ly site.
        var expected = OperatorFixture.GenerateUnits().First(u => u.SiteId == "NV1" && u.StepCode == "EOL");
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "SUB-CTX-1", "{\"value\":12}");

        ProductionUnitContext? own = null;
        ProductionUnitContext? foreign = null;
        await _fixture.SubmitAsync(command, async session =>
        {
            var reader = new ProductionUnitContextReader(session);
            own = await reader.FindAsync(expected.SerialNumber, Ct);
            // Serial của site khác không được nhìn thấy qua session NV1.
            var de1 = OperatorFixture.GenerateUnits().First(u => u.SiteId == "DE1").SerialNumber;
            foreign = await reader.FindAsync(de1, Ct);
            return new CollectionOutcome(true, "OK");
        }, cancellationToken: Ct);

        own.ShouldNotBeNull();
        own!.SiteId.ShouldBe(expected.SiteId);
        own.SerialNumber.ShouldBe(expected.SerialNumber);
        own.UnitKind.ShouldBe(expected.UnitKind);
        own.UnitKind.ShouldNotBeNullOrEmpty();
        own.OperationRunId.ShouldBe(expected.OperationRunId);
        own.StepCode.ShouldBe(expected.StepCode);
        own.EquipmentPath.ShouldBe(expected.EquipmentPath);
        own.ExecutionState.ShouldBe(expected.ExecutionState);
        own.QualityState.ShouldBe(expected.QualityState);
        own.WorkOrderId.ShouldBe(expected.WorkOrderId);
        foreign.ShouldBeNull();
    }

    [Fact]
    public async Task ReseedingContext_InsertsNothingTheSecondTime_AndDoesNotOverwrite()
    {
        // Lần seed đầu đã chạy trong fixture. Sửa một row rồi seed lại: giữ nguyên, chèn 0.
        const string serial = "NV1PP16250A00002";
        await _fixture.ExecuteAsync(
            "UPDATE execution.UnitContext SET ContextJson = JSON_MODIFY(ContextJson, '$.QualityState', 'PreserveMe') WHERE SiteId='NV1' AND SerialNumber=@s;",
            command => command.Parameters.Add("@s", SqlDbType.VarChar, 16).Value = serial, Ct);

        var insertedSecond = await CommandContextFixtureSeed.PrepareAsync(_fixture.ConnectionString, Ct);
        insertedSecond.ShouldBe(0); // reseed idempotent: không chèn lại, không ghi đè.

        var preserved = await _fixture.ScalarAsync(
            "SELECT JSON_VALUE(ContextJson, '$.QualityState') FROM execution.UnitContext WHERE SiteId='NV1' AND SerialNumber=@s;",
            command => command.Parameters.Add("@s", SqlDbType.VarChar, 16).Value = serial, Ct);
        preserved.ShouldBe("PreserveMe");
        await _fixture.ExecuteAsync(
            "UPDATE execution.UnitContext SET ContextJson = JSON_MODIFY(ContextJson, '$.QualityState', 'Pending') WHERE SiteId='NV1' AND SerialNumber=@s;",
            command => command.Parameters.Add("@s", SqlDbType.VarChar, 16).Value = serial, Ct);
    }

    [Fact]
    public async Task AllSeededContextsMatchBothSitesOfTheSharedFixture()
    {
        var expected = OperatorFixture.GenerateUnits().ToDictionary(u => (u.SiteId, u.SerialNumber));
        foreach (var site in new[] { "NV1", "DE1" })
        {
            await using var connection = new SqlConnection(_fixture.ConnectionString);
            await connection.OpenAsync(Ct);
            using var query = new SqlCommand("SELECT ContextJson FROM execution.UnitContext WHERE SiteId=@site", connection);
            query.Parameters.AddWithValue("@site", site);
            using var reader = await query.ExecuteReaderAsync(Ct);
            var count = 0;
            while (await reader.ReadAsync(Ct))
            {
                var actual = System.Text.Json.JsonSerializer.Deserialize<OperatorFixtureUnit>(reader.GetString(0))!;
                actual.ShouldBe(expected[(actual.SiteId, actual.SerialNumber)]);
                count++;
            }
            count.ShouldBe(1000);
        }
    }

    [Fact]
    public async Task RealDispatcherAndDi_ShareHandlerSessionWithStore_AndReplayAcrossProviders()
    {
        var command = SqlCommandStoreFixture.NewCommand("NV1", "op.nv1", "DI-pipeline", "payload");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
            builder.Configuration["NVM_COMMANDS:ConnectionString"] = _fixture.ConnectionString;
            builder.Services.AddNvmKernel(typeof(SqlPipelineHandler).Assembly);
            builder.Services.AddNvmCommandStore(builder.Configuration, builder.Environment);
            using var host = builder.Build();
            await host.StartAsync(Ct);
            await using var scope = host.Services.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandDispatcher>();
            var result = await dispatcher.DispatchAsync(command, Ct);
            result.ReasonCode.ShouldBe("DI-COMMITTED");
            await host.StopAsync(Ct);
        }
        (await _fixture.EffectCountAsync(command, Ct)).ShouldBe(1);
        (await _fixture.OutcomeCountAsync(command, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task RuntimePrincipalCanWriteOutcomesButCannotMutateContextOrDeleteOutcomes()
    {
        await _fixture.ExecuteAsync("CREATE USER nvm_app WITHOUT LOGIN;", _ => { }, Ct);
        await CommandSchemaMigrator.UpgradeAsync(_fixture.ConnectionString, Ct);
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        using var query = new SqlCommand("""
            EXECUTE AS USER='nvm_app';
            SELECT HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','INSERT'),
                   HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','UPDATE'),
                   HAS_PERMS_BY_NAME('command_store.CommandOutcomes','OBJECT','DELETE'),
                   HAS_PERMS_BY_NAME('execution.UnitContext','OBJECT','SELECT'),
                   HAS_PERMS_BY_NAME('execution.UnitContext','OBJECT','INSERT'),
                   HAS_PERMS_BY_NAME('execution.UnitContext','OBJECT','UPDATE'),
                   HAS_PERMS_BY_NAME('execution.UnitContext','OBJECT','DELETE');
            REVERT;
            """, connection);
        using var reader = await query.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        Enumerable.Range(0, 7).Select(reader.GetInt32).ShouldBe(new[] { 1, 1, 0, 1, 0, 0, 0 });
    }
}

public sealed class SqlPipelineHandler(SqlCommandSession session) : ICommandHandler<RecordDataCollection, CollectionOutcome>
{
    public async Task<CollectionOutcome> HandleAsync(RecordDataCollection command, CancellationToken cancellationToken)
    {
        await SqlCommandStoreFixture.InsertEffectAsync(session, command, cancellationToken);
        return new CollectionOutcome(true, "DI-COMMITTED");
    }
}

/// <summary>Command nhập tay bền vững cho test; tên class = token natural key theo ADR-038.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Tên command theo contract RecordDataCollection của ADR-038.")]
public sealed record RecordDataCollection : IDurableCommand, ICommand<CollectionOutcome>
{
    public string CommandType => "RecordDataCollection";
    private readonly IdempotencyKey _key;

    public RecordDataCollection(string siteId, string actorId, string submissionId, string canonicalPayload)
        : this(siteId, actorId, submissionId, canonicalPayload,
            IdempotencyKey.FromNaturalKey(siteId, nameof(RecordDataCollection), submissionId))
    {
    }

    public RecordDataCollection(string siteId, string actorId, string submissionId, string canonicalPayload, IdempotencyKey key)
    {
        SiteId = siteId;
        ActorId = actorId;
        SubmissionId = submissionId;
        CanonicalPayload = canonicalPayload;
        _key = key;
    }

    public string SiteId { get; }

    public string ActorId { get; }

    public string SubmissionId { get; }

    public string CanonicalPayload { get; }

    public IdempotencyKey IdempotencyKey => _key;
}

/// <summary>Kết quả xử lý; accepted=false là rejection nghiệp vụ đã có kết luận, vẫn replay được.</summary>
public sealed record CollectionOutcome(bool Accepted, string ReasonCode);

public sealed record RenamedSubmission(RecordDataCollection Original) : IDurableCommand, ICommand<CollectionOutcome>
{
    public string CommandType => Original.CommandType;
    public string SiteId => Original.SiteId;
    public string ActorId => Original.ActorId;
    public string SubmissionId => Original.SubmissionId;
    public string CanonicalPayload => Original.CanonicalPayload;
    public IdempotencyKey IdempotencyKey => Original.IdempotencyKey;
}

/// <summary>Container SQL Server thật + schema C05 + seed context + bảng effect test.</summary>
public sealed class SqlCommandStoreFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04").Build();

    public string ConnectionString => _sql.GetConnectionString();

    /// <summary>Fallback RAM dùng chung, giống singleton InMemoryIdempotencyStore trong DI thật.</summary>
    public InMemoryIdempotencyStore SharedInMemory { get; } = new(TimeProvider.System);

    public async ValueTask InitializeAsync()
    {
        await _sql.StartAsync();
        // Seed context = migration + 2.000 unit từ cùng generator C03.
        await CommandContextFixtureSeed.PrepareAsync(ConnectionString);
        // Bảng effect chỉ dùng cho test, mô phỏng bản ghi nghiệp vụ commit cùng transaction với claim.
        await ExecuteAsync("""
            IF OBJECT_ID('execution.DataCollectionTest') IS NULL
            CREATE TABLE execution.DataCollectionTest (
                SiteId varchar(3) NOT NULL,
                IdempotencyKey uniqueidentifier NOT NULL,
                Payload nvarchar(400) NOT NULL,
                CONSTRAINT PK_DataCollectionTest PRIMARY KEY (SiteId, IdempotencyKey));
            """, _ => { }, CancellationToken.None);
    }

    public SqlCommandStoreOptions Options(int timeoutSeconds = 30) => new()
    {
        ConnectionString = ConnectionString,
        CommandTimeoutSeconds = timeoutSeconds,
        AllowVolatileCommands = false,
    };

    public static RecordDataCollection NewCommand(string site, string actor, string submissionId, string payload)
        => new(site, actor, submissionId, payload);

    /// <summary>Một "scope" như DI request: session + store + behavior mới, chạy handler qua behavior thật.</summary>
    public async Task<CollectionOutcome> SubmitAsync(
        RecordDataCollection command,
        Func<SqlCommandSession, Task<CollectionOutcome>> handler,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, Options(timeoutSeconds), SharedInMemory);
        var behavior = new IdempotencyBehavior<RecordDataCollection, CollectionOutcome>(store, TimeProvider.System);
        return await behavior.HandleAsync(command, () => handler(session), cancellationToken);
    }

    public static async Task InsertEffectAsync(SqlCommandSession session, RecordDataCollection command, CancellationToken cancellationToken)
    {
        using var effect = session.CreateCommand(
            "INSERT INTO execution.DataCollectionTest(SiteId, IdempotencyKey, Payload) VALUES (@s, @k, @p);");
        effect.Parameters.Add("@s", SqlDbType.VarChar, 3).Value = command.SiteId;
        effect.Parameters.Add("@k", SqlDbType.UniqueIdentifier).Value = command.IdempotencyKey.Value;
        effect.Parameters.Add("@p", SqlDbType.NVarChar, 400).Value = command.CanonicalPayload;
        await effect.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<int> EffectCountAsync(RecordDataCollection command, CancellationToken cancellationToken) =>
        CountAsync("execution.DataCollectionTest", command, cancellationToken);

    public Task<int> OutcomeCountAsync(RecordDataCollection command, CancellationToken cancellationToken) =>
        CountAsync("command_store.CommandOutcomes", command, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Tên bảng chỉ là hai hằng nội bộ; SiteId/key là SQL parameters.")]
    private async Task<int> CountAsync(string table, RecordDataCollection command, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var query = new SqlCommand(
            $"SELECT COUNT(*) FROM {table} WHERE SiteId=@s AND IdempotencyKey=@k;", connection);
        query.Parameters.Add("@s", SqlDbType.VarChar, 3).Value = command.SiteId;
        query.Parameters.Add("@k", SqlDbType.UniqueIdentifier).Value = command.IdempotencyKey.Value;
        return (int)(await query.ExecuteScalarAsync(cancellationToken))!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL hằng từ test; giá trị được bind bằng parameter.")]
    public async Task ExecuteAsync(string sql, Action<SqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = new SqlCommand(sql, connection);
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL hằng từ test; giá trị được bind bằng parameter.")]
    public async Task<string?> ScalarAsync(string sql, Action<SqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = new SqlCommand(sql, connection);
        bind(command);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async ValueTask DisposeAsync() => await _sql.DisposeAsync();
}
