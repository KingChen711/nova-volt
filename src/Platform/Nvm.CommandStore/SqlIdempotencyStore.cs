using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Nvm.Kernel.Commands;
using Nvm.Kernel.Commands.Idempotency;

namespace Nvm.CommandStore;

/// <summary>Claim, effect và outcome cùng SQL transaction. Fallback RAM chỉ dành cho command dev cũ.</summary>
public sealed class SqlIdempotencyStore(
    SqlCommandSession session,
    SqlCommandStoreOptions options,
    InMemoryIdempotencyStore developmentStore) : IContextualIdempotencyStore
{
    private ICommand? _command;
    private int _busy;

    /// <inheritdoc />
    public void Prepare(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not IDurableCommand && !options.AllowVolatileCommands)
        {
            throw new InvalidOperationException("Volatile commands are only available in Development.");
        }


        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent or nested commands require separate scopes.");
        }
        _command = command;
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim<TResult>> ClaimAsync<TResult>(IdempotencyKey key, string commandType, CancellationToken cancellationToken)
    {
        try
        {
            if (_command is null || _command.IdempotencyKey != key)
            {
                throw new InvalidOperationException("Prepare must supply the command being claimed.");
            }

            if (_command is not IDurableCommand identity)
            {
                var legacy = await developmentStore.ClaimAsync<TResult>(key, commandType, cancellationToken).ConfigureAwait(false);
                if (!legacy.IsGranted)
                { Interlocked.Exchange(ref _busy, 0); }
                return legacy;
            }

            Validate(identity, commandType, key);
            if (options.CommandTimeoutSeconds is < 1 or > 300)
            {
                throw new InvalidOperationException("Command timeout must be between 1 and 300 seconds.");
            }
            await session.BeginAsync(options.ConnectionString, identity.SiteId, cancellationToken).ConfigureAwait(false);
            // Range lock giữ cả trường hợp chưa có row; unique PK là ràng buộc cuối tại database.
            using var read = Create("""
                SELECT ActorId, CommandType, PayloadHash, ResultType, OutcomeJson, HandledAt
                FROM command_store.CommandOutcomes WITH (UPDLOCK, HOLDLOCK)
                WHERE SiteId = @site AND IdempotencyKey = @key;
                """, identity.SiteId, key);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(identity.CanonicalPayload));
            var resultType = typeof(TResult).FullName ?? typeof(TResult).Name;
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Đọc bất đồng bộ mọi cột (CA1849): các API GetString/GetFieldValue đồng bộ chặn thread
                // trong lúc chờ dữ liệu từ TDS stream.
                var storedActor = await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);
                var storedType = await reader.GetFieldValueAsync<string>(1, cancellationToken).ConfigureAwait(false);
                var storedHash = await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false);
                var storedResultType = await reader.GetFieldValueAsync<string>(3, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(storedActor, identity.ActorId, StringComparison.Ordinal)
                    || !string.Equals(storedType, commandType, StringComparison.Ordinal)
                    || !fingerprint.AsSpan().SequenceEqual(storedHash)
                    || !string.Equals(storedResultType, resultType, StringComparison.Ordinal))
                {
                    // Cùng khoá nhưng actor/loại/payload khác: không trả outcome của người khác, cũng
                    // không ghi đè. Đây là lỗi định danh, nói to lên chứ không nuốt.
                    throw new InvalidOperationException("Idempotency identity or payload conflict.");
                }

                var outcomeJson = await reader.GetFieldValueAsync<string>(4, cancellationToken).ConfigureAwait(false);
                var handledAt = await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken).ConfigureAwait(false);
                var outcome = new IdempotentOutcome<TResult>(JsonSerializer.Deserialize<TResult>(outcomeJson)!, handledAt);
                await reader.CloseAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                Interlocked.Exchange(ref _busy, 0);
                return IdempotencyClaim.Replay(outcome);
            }

            await reader.CloseAsync().ConfigureAwait(false);
            using var insert = Create("""
                INSERT INTO command_store.CommandOutcomes
                    (SiteId, IdempotencyKey, ActorId, CommandType, PayloadHash, ResultType)
                VALUES (@site, @key, @actor, @type, @hash, @resultType);
                """, identity.SiteId, key);
            insert.Parameters.Add("@actor", SqlDbType.NVarChar, 200).Value = identity.ActorId;
            insert.Parameters.Add("@type", SqlDbType.NVarChar, 200).Value = commandType;
            insert.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = fingerprint;
            insert.Parameters.Add("@resultType", SqlDbType.NVarChar, 500).Value = resultType;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return IdempotencyClaim.Granted<TResult>();
        }
        catch
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync<TResult>(IdempotencyKey key, TResult result, DateTimeOffset handledAt, CancellationToken cancellationToken)
    {
        EnsureOwner(key);
        if (_command is not IDurableCommand identity)
        {
            await developmentStore.CompleteAsync(key, result, handledAt, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var update = Create("""
                UPDATE command_store.CommandOutcomes SET OutcomeJson = @outcome, HandledAt = @at
                WHERE SiteId = @site AND IdempotencyKey = @key AND OutcomeJson IS NULL;
                """, identity.SiteId, key);
            update.Parameters.Add("@outcome", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(result);
            update.Parameters.Add("@at", SqlDbType.DateTimeOffset).Value = handledAt;
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The active claim was not found.");
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _busy, 0);
    }

    /// <inheritdoc />
    public async Task AbandonAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        EnsureOwner(key);
        try
        {
            if (_command is IDurableCommand)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await developmentStore.AbandonAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void EnsureOwner(IdempotencyKey key)
    {
        if (Volatile.Read(ref _busy) == 0 || _command?.IdempotencyKey != key)
        {
            throw new InvalidOperationException("This scope does not own the claim.");
        }
    }

    private SqlCommand Create(string sql, string site, IdempotencyKey key)
    {
        var command = session.CreateCommand(sql);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.Add("@site", SqlDbType.VarChar, 3).Value = site;
        command.Parameters.Add("@key", SqlDbType.UniqueIdentifier).Value = key.Value;
        return command;
    }

    private static void Validate(IDurableCommand identity, string commandType, IdempotencyKey key)
    {
        // Không hardcode danh sách site (NV1/DE1): site đã do principal cung cấp và K3 ép filter ở tầng
        // trên; ở đây chỉ kiểm ràng buộc CẤU TRÚC để dữ liệu vừa cột và natural key khớp. Cột SiteId là
        // varchar(3), nên một site dài hơn là dữ liệu hỏng chứ không phải một site hợp lệ khác.
        if (string.IsNullOrWhiteSpace(identity.SiteId) || identity.SiteId.Length != 3
            || identity.SiteId.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            || string.IsNullOrWhiteSpace(identity.ActorId) || identity.ActorId.Length > 200
            || string.IsNullOrWhiteSpace(identity.SubmissionId)
            || string.IsNullOrEmpty(identity.CanonicalPayload) || string.IsNullOrWhiteSpace(commandType) || commandType.Length > 200
            || !string.Equals(commandType, identity.CommandType, StringComparison.Ordinal)
            || key != IdempotencyKey.FromNaturalKey(identity.SiteId, commandType, identity.SubmissionId))
        {
            // Backend suy lại khoá từ (site, commandType, submissionId) và bác nếu client gửi khoá lệch:
            // một client không tự chọn được khoá để va vào bản ghi của người khác.
            throw new ArgumentException("Invalid command identity or natural key.");
        }
    }
}
