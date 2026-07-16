namespace SocialGraph.Api.Infrastructure.Outbox;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using SocialGraph.Api.Database;

public sealed class PostgresIntegrationOutboxStore : IIntegrationOutboxStore
{
    private readonly MyDbContext _dbContext;
    private readonly IntegrationOutboxOptions _options;

    public PostgresIntegrationOutboxStore(
        MyDbContext dbContext,
        IOptions<IntegrationOutboxOptions> options)
    {
        _dbContext = dbContext;
        _options = options.Value;
    }

    public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (IsInMemory())
        {
            return Task.CompletedTask;
        }

        return _dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS social_graph.integration_outbox (
                id uuid PRIMARY KEY,
                event_type varchar(100) NOT NULL,
                aggregate_id bigint NULL,
                idempotency_key varchar(200) NOT NULL,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                available_at timestamptz NOT NULL,
                attempts integer NOT NULL DEFAULT 0,
                max_attempts integer NOT NULL,
                status smallint NOT NULL DEFAULT 0,
                locked_at timestamptz NULL,
                locked_by varchar(200) NULL,
                last_error varchar(2000) NULL,
                completed_at timestamptz NULL,
                CONSTRAINT ux_integration_outbox_idempotency_key UNIQUE (idempotency_key),
                CONSTRAINT ck_integration_outbox_status CHECK (status BETWEEN 0 AND 3),
                CONSTRAINT ck_integration_outbox_attempts CHECK (attempts >= 0 AND max_attempts > 0)
            );
            CREATE INDEX IF NOT EXISTS ix_integration_outbox_dispatch
                ON social_graph.integration_outbox (status, available_at, created_at);
            """,
            cancellationToken);
    }

    public async Task<IntegrationOutboxMessage> EnqueueAsync(
        string eventType,
        long? aggregateId,
        string idempotencyKey,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var existing = await _dbContext.IntegrationOutboxTb
            .FirstOrDefaultAsync(item => item.idempotency_key == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new IntegrationOutboxMessage
        {
            id = Guid.NewGuid(),
            event_type = eventType,
            aggregate_id = aggregateId,
            idempotency_key = idempotencyKey,
            payload = payload,
            created_at = now,
            available_at = now,
            attempts = 0,
            max_attempts = Math.Clamp(_options.MaxAttempts, 1, 100),
            status = IntegrationOutboxStatus.Pending
        };

        if (IsInMemory())
        {
            _dbContext.IntegrationOutboxTb.Add(message);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return message;
        }

        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO social_graph.integration_outbox
                (id, event_type, aggregate_id, idempotency_key, payload, created_at, available_at,
                 attempts, max_attempts, status)
            VALUES
                (@id, @eventType, @aggregateId, @idempotencyKey, @payload, @createdAt, @availableAt,
                 0, @maxAttempts, @status)
            ON CONFLICT (idempotency_key) DO NOTHING;
            """,
            new object[]
            {
                new NpgsqlParameter("id", message.id),
                new NpgsqlParameter("eventType", message.event_type),
                new NpgsqlParameter("aggregateId", (object?)message.aggregate_id ?? DBNull.Value),
                new NpgsqlParameter("idempotencyKey", message.idempotency_key),
                new NpgsqlParameter("payload", message.payload) { NpgsqlDbType = NpgsqlDbType.Jsonb },
                new NpgsqlParameter("createdAt", message.created_at),
                new NpgsqlParameter("availableAt", message.available_at),
                new NpgsqlParameter("maxAttempts", message.max_attempts),
                new NpgsqlParameter("status", message.status)
            },
            cancellationToken);

        return await _dbContext.IntegrationOutboxTb
            .FirstAsync(item => item.idempotency_key == idempotencyKey, cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationOutboxMessage>> ClaimAsync(
        string workerId,
        int batchSize,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleBefore = now - lockTimeout;
        var take = Math.Clamp(batchSize, 1, 100);
        await using var transaction = IsInMemory()
            ? null
            : await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        List<IntegrationOutboxMessage> messages;
        if (IsInMemory())
        {
            messages = await _dbContext.IntegrationOutboxTb
                .Where(item =>
                    (item.status == IntegrationOutboxStatus.Pending && item.available_at <= now) ||
                    (item.status == IntegrationOutboxStatus.Processing && item.locked_at < staleBefore))
                .OrderBy(item => item.created_at)
                .Take(take)
                .ToListAsync(cancellationToken);
        }
        else
        {
            messages = await _dbContext.IntegrationOutboxTb
                .FromSqlRaw(
                    """
                    SELECT *
                    FROM social_graph.integration_outbox
                    WHERE (status = {0} AND available_at <= {1})
                       OR (status = {2} AND locked_at < {3})
                    ORDER BY created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT {4}
                    """,
                    IntegrationOutboxStatus.Pending,
                    now,
                    IntegrationOutboxStatus.Processing,
                    staleBefore,
                    take)
                .ToListAsync(cancellationToken);
        }

        foreach (var message in messages)
        {
            message.status = IntegrationOutboxStatus.Processing;
            message.locked_at = now;
            message.locked_by = workerId;
            message.attempts++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return messages;
    }

    public Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(id, message =>
        {
            message.status = IntegrationOutboxStatus.Completed;
            message.completed_at = DateTimeOffset.UtcNow;
            message.locked_at = null;
            message.locked_by = null;
            message.last_error = null;
        }, cancellationToken);
    }

    public Task MarkFailedAsync(
        Guid id,
        string error,
        TimeSpan delay,
        bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(id, message =>
        {
            message.status = deadLetter ? IntegrationOutboxStatus.DeadLetter : IntegrationOutboxStatus.Pending;
            message.available_at = DateTimeOffset.UtcNow + delay;
            message.locked_at = null;
            message.locked_by = null;
            message.last_error = error.Length <= 2000 ? error : error[..2000];
        }, cancellationToken);
    }

    public Task ReleaseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return UpdateAsync(id, message =>
        {
            message.status = IntegrationOutboxStatus.Pending;
            message.available_at = DateTimeOffset.UtcNow;
            message.locked_at = null;
            message.locked_by = null;
        }, cancellationToken);
    }

    public async Task DeleteCompletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        if (IsInMemory())
        {
            var completed = await _dbContext.IntegrationOutboxTb
                .Where(item => item.status == IntegrationOutboxStatus.Completed && item.completed_at < cutoff)
                .ToListAsync(cancellationToken);
            _dbContext.IntegrationOutboxTb.RemoveRange(completed);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        await _dbContext.IntegrationOutboxTb
            .Where(item => item.status == IntegrationOutboxStatus.Completed && item.completed_at < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationOutboxMessage>> ListDeadLettersAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.IntegrationOutboxTb
            .AsNoTracking()
            .Where(item => item.status == IntegrationOutboxStatus.DeadLetter)
            .OrderByDescending(item => item.created_at)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RequeueDeadLetterAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await _dbContext.IntegrationOutboxTb
            .FirstOrDefaultAsync(
                item => item.id == id && item.status == IntegrationOutboxStatus.DeadLetter,
                cancellationToken);
        if (message is null)
        {
            return false;
        }

        message.status = IntegrationOutboxStatus.Pending;
        message.attempts = 0;
        message.available_at = DateTimeOffset.UtcNow;
        message.locked_at = null;
        message.locked_by = null;
        message.last_error = null;
        message.completed_at = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task UpdateAsync(
        Guid id,
        Action<IntegrationOutboxMessage> update,
        CancellationToken cancellationToken)
    {
        var message = await _dbContext.IntegrationOutboxTb.FirstOrDefaultAsync(item => item.id == id, cancellationToken);
        if (message is null)
        {
            return;
        }

        update(message);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool IsInMemory() => string.Equals(
        _dbContext.Database.ProviderName,
        "Microsoft.EntityFrameworkCore.InMemory",
        StringComparison.Ordinal);
}
