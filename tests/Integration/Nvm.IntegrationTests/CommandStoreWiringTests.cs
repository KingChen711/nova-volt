using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Nvm.CommandStore;
using Nvm.Kernel;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.IntegrationTests;

/// <summary>
/// C05 wiring: guard Production và fallback command volatile. Không cần Docker — chỉ kiểm DI và nhánh
/// Prepare, không mở connection SQL nào.
/// </summary>
public sealed class CommandStoreWiringTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Production_WithoutConnectionString_RefusesToRegister()
    {
        var services = new ServiceCollection();
        services.AddNvmKernel();
        Should.Throw<InvalidOperationException>(() =>
                services.AddNvmCommandStore(Config(connectionString: null), Environment("Production")))
            .Message.ShouldContain("in-memory command storage is forbidden");
    }

    [Fact]
    public async Task Production_WithSqlStore_PassesTheStartupGuard()
    {
        var services = new ServiceCollection();
        services.AddNvmKernel();
        services.AddNvmCommandStore(Config("Server=nowhere;Database=x;"), Environment("Production"));
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        // Guard chỉ resolve store (không chạm DB); SqlIdempotencyStore đã thay InMemory → không ném.
        await StartGuardsAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().ShouldBeOfType<SqlIdempotencyStore>();
    }

    [Fact]
    public async Task Production_WithInMemoryStoreReinstated_IsRejectedByTheGuard()
    {
        var services = new ServiceCollection();
        services.AddNvmKernel();
        services.AddNvmCommandStore(Config("Server=nowhere;Database=x;"), Environment("Production"));
        // Mô phỏng cấu hình sai: một đăng ký muộn kéo store về RAM.
        services.AddScoped<IIdempotencyStore, InMemoryIdempotencyStore>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() => StartGuardsAsync(provider));
        thrown.Message.ShouldContain("Production requires SQL command storage");
    }

    [Fact]
    public async Task Development_WithoutConnectionString_IsAllowed()
    {
        var services = new ServiceCollection();
        services.AddNvmKernel();
        services.AddNvmCommandStore(Config(connectionString: null), Environment("Development"));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await StartGuardsAsync(provider); // Development không bị guard chặn.

        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SqlCommandStoreOptions>().AllowVolatileCommands.ShouldBeTrue();
    }

    [Fact]
    public async Task VolatileCommand_OutsideDevelopment_IsRejectedByPrepare_WithoutTouchingSql()
    {
        var options = new SqlCommandStoreOptions { ConnectionString = "", AllowVolatileCommands = false };
        var store = new SqlIdempotencyStore(new SqlCommandSession(), options, new InMemoryIdempotencyStore(TimeProvider.System));
        var behavior = new IdempotencyBehavior<TestVolatileCommand, int>(store, TimeProvider.System);
        var command = new TestVolatileCommand(IdempotencyKey.FromNaturalKey("prod-volatile"));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await behavior.HandleAsync(command, () => Task.FromResult(1), Ct));
        thrown.Message.ShouldContain("only available in Development");
    }

    [Fact]
    public async Task VolatileCommand_InDevelopment_UsesInMemoryFallback_AndStillDedupes()
    {
        var options = new SqlCommandStoreOptions { ConnectionString = "", AllowVolatileCommands = true };
        var shared = new InMemoryIdempotencyStore(TimeProvider.System);
        var command = new TestVolatileCommand(IdempotencyKey.FromNaturalKey("dev-volatile"));
        var handlerCalls = 0;

        async Task<int> DispatchInFreshScope()
        {
            var store = new SqlIdempotencyStore(new SqlCommandSession(), options, shared);
            var behavior = new IdempotencyBehavior<TestVolatileCommand, int>(store, TimeProvider.System);
            return await behavior.HandleAsync(command, () =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(7);
            }, Ct);
        }

        (await DispatchInFreshScope()).ShouldBe(7);
        (await DispatchInFreshScope()).ShouldBe(7); // replay từ RAM dùng chung, không chạy handler lần hai.
        handlerCalls.ShouldBe(1);
    }

    private static async Task StartGuardsAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static IConfiguration Config(string? connectionString) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NVM_COMMANDS:ConnectionString"] = connectionString,
        }).Build();

    private static FakeEnvironment Environment(string name) => new() { EnvironmentName = name };

    [Fact]
    public async Task SimultaneousPrepareInOneScope_AllowsExactlyOneOwner()
    {
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, new SqlCommandStoreOptions(), new InMemoryIdempotencyStore(TimeProvider.System));
        var command = new RecordDataCollection("NV1", "actor", "same-scope", "payload");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owners = 0;
        var refused = 0;
        var calls = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            { store.Prepare(command); Interlocked.Increment(ref owners); }
            catch (InvalidOperationException) { Interlocked.Increment(ref refused); }
        }, Ct)).ToArray();
        start.SetResult();
        await Task.WhenAll(calls);
        owners.ShouldBe(1);
        refused.ShouldBe(31);
        await store.AbandonAsync(command.IdempotencyKey, Ct);
        store.Prepare(command);
        await store.AbandonAsync(command.IdempotencyKey, Ct);
    }

    [Theory]
    [InlineData("NV ")]
    [InlineData("nv1")]
    [InlineData("NＶ1")]
    public async Task NonCanonicalSiteCannotBeSilentlyConvertedBySql(string site)
    {
        await using var session = new SqlCommandSession();
        var store = new SqlIdempotencyStore(session, new SqlCommandStoreOptions(), new InMemoryIdempotencyStore(TimeProvider.System));
        var command = new RecordDataCollection(site, "actor", "invalid-site", "payload");
        store.Prepare(command);
        await Should.ThrowAsync<ArgumentException>(() => store.ClaimAsync<int>(command.IdempotencyKey, nameof(RecordDataCollection), Ct));
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Nvm.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>Command dev cũ, hiệu ứng in-memory; không phải <see cref="IDurableCommand"/>.</summary>
public sealed record TestVolatileCommand(IdempotencyKey IdempotencyKey) : ICommand<int>;
