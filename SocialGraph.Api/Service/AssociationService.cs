namespace SocialGraph.Api.Service;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SocialGraph.Api.Database;
using StackExchange.Redis;

public sealed class AssociationService : IAssociationService
{
    private static readonly IReadOnlyDictionary<short, short> InverseAssociationTypes = new Dictionary<short, short>
    {
        [0] = 0,
        [1] = 2,
        [3] = 4,
        [5] = 6,
        [9] = 10,
        [11] = 12,
        [13] = 14,
        [15] = 16,
        [17] = 18,
        [23] = 24
    };

    private readonly MyDbContext _dbContext;
    private readonly IDatabase _redis;

    public AssociationService(MyDbContext dbContext, IConnectionMultiplexer redis)
    {
        _dbContext = dbContext;
        _redis = redis.GetDatabase();
    }

    public async Task<bool> AddAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken = default)
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await UpsertAssociationAsync(id1, atype, id2, time, cancellationToken);

        if (TryGetInverseAssociationType(atype, out var inverseAtype) && !IsSameAssociation(id1, atype, id2, id2, inverseAtype, id1))
        {
            await UpsertAssociationAsync(id2, inverseAtype, id1, time, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await AddToCacheOrHydrateAsync(id1, atype, id2, time, cancellationToken);
        if (TryGetInverseAssociationType(atype, out inverseAtype))
        {
            await AddToCacheOrHydrateAsync(id2, inverseAtype, id1, time, cancellationToken);
        }

        return true;
    }

    public async Task<bool> DeleteOneAssociationAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var rows = await DeleteAssociationRowAsync(id1, atype, id2, cancellationToken);

        if (TryGetInverseAssociationType(atype, out var inverseAtype) && !IsSameAssociation(id1, atype, id2, id2, inverseAtype, id1))
        {
            rows += await DeleteAssociationRowAsync(id2, inverseAtype, id1, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await RemoveFromCacheIfLoadedAsync(id1, atype, id2);
        if (TryGetInverseAssociationType(atype, out inverseAtype))
        {
            await RemoveFromCacheIfLoadedAsync(id2, inverseAtype, id1);
        }

        return rows > 0;
    }

    public async Task<bool> DeleteAllAssociationAsync(
        long id1,
        short atype,
        CancellationToken cancellationToken = default)
    {
        List<long> inverseIds = [];
        if (TryGetInverseAssociationType(atype, out var inverseAtype))
        {
            inverseIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == id1 && item.atype == atype)
                .Select(item => item.id2)
                .ToListAsync(cancellationToken);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id1 AND atype = @atype",
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype)
            },
            cancellationToken);

        if (TryGetInverseAssociationType(atype, out inverseAtype))
        {
            rows += await _dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM social_graph.associations WHERE atype = @atype AND id2 = @id2",
                new object[]
                {
                    new NpgsqlParameter("atype", inverseAtype),
                    new NpgsqlParameter("id2", id1)
                },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await DeleteAssociationCacheAsync(id1, atype);
        if (TryGetInverseAssociationType(atype, out inverseAtype))
        {
            foreach (var inverseId in inverseIds)
            {
                await RemoveFromCacheIfLoadedAsync(inverseId, inverseAtype, id1);
            }
        }

        return rows > 0;
    }

    public async Task<int> DeleteObjectAssociationsAsync(long objectId, CancellationToken cancellationToken = default)
    {
        var affectedKeys = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == objectId || item.id2 == objectId)
            .Select(item => new { item.id1, item.atype })
            .Distinct()
            .ToListAsync(cancellationToken);

        var rows = await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id OR id2 = @id",
            new object[] { new NpgsqlParameter("id", objectId) },
            cancellationToken);

        foreach (var key in affectedKeys)
        {
            await DeleteAssociationCacheAsync(key.id1, key.atype);
        }

        return rows;
    }

    public async Task<long> CountAssociationAsync(
        long id1,
        short atype,
        CancellationToken cancellationToken = default)
    {
        await EnsureAssociationCacheAsync(id1, atype, cancellationToken);
        return await _redis.SortedSetLengthAsync(AssociationKey(id1, atype));
    }

    public async Task<AssociationPageResult> RetrieveAssociationAsync(
        long id1,
        short atype,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var skip = ParseCursor(cursor);
        var take = Math.Clamp(limit, 1, 100);

        await EnsureAssociationCacheAsync(id1, atype, cancellationToken);

        var key = AssociationKey(id1, atype);
        var entries = await _redis.SortedSetRangeByRankWithScoresAsync(
            key,
            skip,
            skip + take - 1,
            Order.Descending);

        var items = entries
            .Select(entry => new AssociationEdgeResult(ParseId(entry.Element), (long)entry.Score))
            .ToArray();

        var total = await _redis.SortedSetLengthAsync(key);
        var nextOffset = skip + items.Length;
        var nextCursor = nextOffset < total
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;

        return new AssociationPageResult(items, nextCursor);
    }

    private async Task UpsertAssociationAsync(
        long id1,
        short atype,
        long id2,
        long time,
        CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO social_graph.associations (id1, atype, id2, time)
            VALUES (@id1, @atype, @id2, @time)
            ON CONFLICT (id1, atype, id2) DO UPDATE SET time = EXCLUDED.time
            """,
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype),
                new NpgsqlParameter("id2", id2),
                new NpgsqlParameter("time", time)
            },
            cancellationToken);
    }

    private async Task<int> DeleteAssociationRowAsync(
        long id1,
        short atype,
        long id2,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM social_graph.associations WHERE id1 = @id1 AND atype = @atype AND id2 = @id2",
            new object[]
            {
                new NpgsqlParameter("id1", id1),
                new NpgsqlParameter("atype", atype),
                new NpgsqlParameter("id2", id2)
            },
            cancellationToken);
    }

    private async Task EnsureAssociationCacheAsync(long id1, short atype, CancellationToken cancellationToken)
    {
        if (await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            return;
        }

        await HydrateAssociationCacheAsync(id1, atype, cancellationToken);
    }

    private async Task HydrateAssociationCacheAsync(long id1, short atype, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == id1 && item.atype == atype)
            .Select(item => new { item.id2, item.time })
            .ToListAsync(cancellationToken);

        var key = AssociationKey(id1, atype);
        await _redis.KeyDeleteAsync(key);

        if (rows.Count > 0)
        {
            var entries = rows
                .Select(item => new SortedSetEntry(ToRedisValue(item.id2), item.time))
                .ToArray();

            await _redis.SortedSetAddAsync(key, entries);
        }

        await _redis.StringSetAsync(AssociationMarkerKey(id1, atype), "1");
    }

    private async Task AddToCacheOrHydrateAsync(
        long id1,
        short atype,
        long id2,
        long time,
        CancellationToken cancellationToken)
    {
        if (await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            await _redis.SortedSetAddAsync(AssociationKey(id1, atype), ToRedisValue(id2), time);
            return;
        }

        await HydrateAssociationCacheAsync(id1, atype, cancellationToken);
    }

    private async Task RemoveFromCacheIfLoadedAsync(long id1, short atype, long id2)
    {
        if (!await _redis.KeyExistsAsync(AssociationMarkerKey(id1, atype)))
        {
            return;
        }

        await _redis.SortedSetRemoveAsync(AssociationKey(id1, atype), ToRedisValue(id2));
    }

    private async Task DeleteAssociationCacheAsync(long id1, short atype)
    {
        await _redis.KeyDeleteAsync(new RedisKey[]
        {
            AssociationKey(id1, atype),
            AssociationMarkerKey(id1, atype)
        });
    }

    private static bool TryGetInverseAssociationType(short atype, out short inverseAtype)
    {
        return InverseAssociationTypes.TryGetValue(atype, out inverseAtype);
    }

    private static bool IsSameAssociation(long id1, short atype, long id2, long otherId1, short otherAtype, long otherId2)
    {
        return id1 == otherId1 && atype == otherAtype && id2 == otherId2;
    }

    private static long ParseCursor(string? cursor)
    {
        return long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0
            ? offset
            : 0;
    }

    private static long ParseId(RedisValue value)
    {
        return long.Parse(value.ToString(), CultureInfo.InvariantCulture);
    }

    private static RedisValue ToRedisValue(long id)
    {
        return id.ToString(CultureInfo.InvariantCulture);
    }

    private static RedisKey AssociationKey(long id1, short atype)
    {
        return $"{id1.ToString(CultureInfo.InvariantCulture)}:{atype.ToString(CultureInfo.InvariantCulture)}";
    }

    private static RedisKey AssociationMarkerKey(long id1, short atype)
    {
        return $"{AssociationKey(id1, atype)}:cached";
    }
}
