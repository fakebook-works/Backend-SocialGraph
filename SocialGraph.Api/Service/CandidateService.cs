namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class CandidateService : ICandidateService
{
    private readonly MyDbContext _dbContext;
    private readonly IAssociationService _associationService;

    public CandidateService(
        MyDbContext dbContext,
        IAssociationService associationService)
    {
        _dbContext = dbContext;
        _associationService = associationService;
    }

    public async Task<IReadOnlyList<long>> GetPostCandidateIdsAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var blocked = await GetBlockedUserIdsAsync(userId, cancellationToken);
        var friends = await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 200, cancellationToken);
        var followed = await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 200, cancellationToken);
        var groupIds = await GetUserGroupIdsAsync(userId, cancellationToken);
        var candidates = new HashSet<long>();

        await AddAuthoredPostIdsAsync(candidates, friends, blocked, take, maxVisiblePrivacy: 2, cancellationToken);
        await AddAuthoredPostIdsAsync(candidates, followed, blocked, take, maxVisiblePrivacy: 1, cancellationToken);
        await AddGroupPostIdsAsync(candidates, groupIds, blocked, take, cancellationToken);
        await AddRecentPublicFeedPostIdsAsync(candidates, blocked, take, cancellationToken);
        await AddPublicGroupPostIdsAsync(candidates, blocked, take, cancellationToken);

        return candidates
            .OrderByDescending(id => id)
            .Take(take)
            .ToArray();
    }

    public async Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var blocked = await GetBlockedUserIdsAsync(userId, cancellationToken);
        var candidates = new Dictionary<long, CandidateItemResult>();

        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 200, cancellationToken), GraphObjectType.Reel, "friend", blocked, take, maxVisiblePrivacy: 2, cancellationToken: cancellationToken);
        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 200, cancellationToken), GraphObjectType.Reel, "followed", blocked, take, maxVisiblePrivacy: 1, cancellationToken: cancellationToken);
        await AddRecentCandidatesAsync(candidates, GraphObjectType.Reel, "recent_public", blocked, take, cancellationToken);

        return candidates.Values
            .OrderByDescending(item => item.Id)
            .Take(take)
            .ToArray();
    }

    private async Task AddAuthoredPostIdsAsync(
        HashSet<long> candidates,
        IReadOnlyList<long> authorIds,
        ISet<long> blocked,
        int limit,
        int maxVisiblePrivacy,
        CancellationToken cancellationToken)
    {
        if (authorIds.Count == 0)
        {
            return;
        }

        var rows = await (
            from authored in _dbContext.AssociationsTb.AsNoTracking()
            join post in _dbContext.ObjectsTb.AsNoTracking() on authored.id2 equals post.id
            where authorIds.Contains(authored.id1) &&
                authored.atype == GraphAssociationType.Authored &&
                (post.otype == GraphObjectType.FeedPost || post.otype == GraphObjectType.Reel)
            orderby post.id descending
            select new { PostId = post.id, AuthorId = authored.id1, post.data })
            .Take(limit * 3)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (blocked.Contains(row.AuthorId))
            {
                continue;
            }

            var privacy = GraphJson.Int(GraphJson.ParseObject(row.data), "privacy");
            if (privacy < 0 || privacy > maxVisiblePrivacy)
            {
                continue;
            }

            candidates.Add(row.PostId);
        }
    }

    private async Task AddGroupPostIdsAsync(
        HashSet<long> candidates,
        IReadOnlyList<long> groupIds,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return;
        }

        var rows = await (
            from published in _dbContext.AssociationsTb.AsNoTracking()
            join post in _dbContext.ObjectsTb.AsNoTracking() on published.id2 equals post.id
            join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
            where groupIds.Contains(published.id1) &&
                published.atype == GraphAssociationType.Published &&
                post.otype == GraphObjectType.GroupPost &&
                authoredBy.atype == GraphAssociationType.AuthoredBy
            orderby post.id descending
            select new { PostId = post.id, AuthorId = authoredBy.id2 })
            .Take(limit * 3)
            .ToListAsync(cancellationToken);

        foreach (var row in rows.Where(row => !blocked.Contains(row.AuthorId)))
        {
            candidates.Add(row.PostId);
        }
    }

    private async Task AddRecentPublicFeedPostIdsAsync(
        HashSet<long> candidates,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from post in _dbContext.ObjectsTb.AsNoTracking()
            join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
            where (post.otype == GraphObjectType.FeedPost || post.otype == GraphObjectType.Reel) &&
                authoredBy.atype == GraphAssociationType.AuthoredBy
            orderby post.id descending
            select new { PostId = post.id, AuthorId = authoredBy.id2, post.data })
            .Take(limit * 3)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (blocked.Contains(row.AuthorId) ||
                GraphJson.Int(GraphJson.ParseObject(row.data), "privacy") != 0)
            {
                continue;
            }

            candidates.Add(row.PostId);
        }
    }

    private async Task AddPublicGroupPostIdsAsync(
        HashSet<long> candidates,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from published in _dbContext.AssociationsTb.AsNoTracking()
            join post in _dbContext.ObjectsTb.AsNoTracking() on published.id2 equals post.id
            join groupObject in _dbContext.ObjectsTb.AsNoTracking() on published.id1 equals groupObject.id
            join authoredBy in _dbContext.AssociationsTb.AsNoTracking() on post.id equals authoredBy.id1
            where published.atype == GraphAssociationType.Published &&
                post.otype == GraphObjectType.GroupPost &&
                groupObject.otype == GraphObjectType.Group &&
                authoredBy.atype == GraphAssociationType.AuthoredBy
            orderby post.id descending
            select new
            {
                PostId = post.id,
                AuthorId = authoredBy.id2,
                GroupData = groupObject.data
            })
            .Take(limit * 6)
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (blocked.Contains(row.AuthorId) ||
                GraphJson.Int(GraphJson.ParseObject(row.GroupData), "privacy") != 0)
            {
                continue;
            }

            candidates.Add(row.PostId);
        }
    }

    private async Task AddAuthorCandidatesAsync(
        Dictionary<long, CandidateItemResult> candidates,
        IReadOnlyList<long> authorIds,
        short objectType,
        string source,
        ISet<long> blocked,
        int limit,
        int maxVisiblePrivacy,
        CancellationToken cancellationToken)
    {
        var eligibleAuthors = authorIds.Where(id => !blocked.Contains(id)).Distinct().ToArray();
        if (eligibleAuthors.Length == 0)
        {
            return;
        }

        var perAuthor = Math.Max(5, limit / 2);
        var rows = await GetNewestPerAuthorAsync(
            eligibleAuthors,
            objectType,
            perAuthor,
            cancellationToken);

        foreach (var row in rows)
        {
            var privacy = GraphJson.Int(GraphJson.ParseObject(row.Data), "privacy");
            if (privacy < 0 || privacy > maxVisiblePrivacy)
            {
                continue;
            }

            AddCandidate(candidates, row.Id, row.AuthorId, row.Data, source, blocked);
        }
    }

    private sealed record AuthoredCandidateRow(long Id, string Data, long AuthorId);

    /// <summary>
    /// The newest <paramref name="perAuthor"/> objects of the given type for each author.
    /// </summary>
    /// <remarks>
    /// This ran one query per author, and the caller passes up to a few hundred of them for a
    /// single feed request. A window function answers the whole set at once while keeping the
    /// per-author limit exact, so one prolific author cannot crowd everyone else out — which a
    /// single globally-ordered query with one overall limit would allow.
    /// </remarks>
    private async Task<IReadOnlyList<AuthoredCandidateRow>> GetNewestPerAuthorAsync(
        long[] authorIds,
        short objectType,
        int perAuthor,
        CancellationToken cancellationToken)
    {
        if (IsInMemory())
        {
            // The in-memory provider cannot translate a window function; tests run against
            // fixtures small enough that the original per-author queries are fine.
            var results = new List<AuthoredCandidateRow>();
            foreach (var authorId in authorIds)
            {
                var authored = await (
                    from association in _dbContext.AssociationsTb.AsNoTracking()
                    join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
                    where association.id1 == authorId &&
                        association.atype == GraphAssociationType.Authored &&
                        obj.otype == objectType
                    orderby association.time descending
                    select new AuthoredCandidateRow(obj.id, obj.data, authorId))
                    .Take(perAuthor)
                    .ToListAsync(cancellationToken);
                results.AddRange(authored);
            }

            return results;
        }

        return await _dbContext.Database
            .SqlQuery<AuthoredCandidateRow>($"""
                SELECT "Id", "Data", "AuthorId"
                FROM (
                    SELECT o.id AS "Id",
                           o.data AS "Data",
                           a.id1 AS "AuthorId",
                           ROW_NUMBER() OVER (PARTITION BY a.id1 ORDER BY a."time" DESC) AS rn
                    FROM social_graph.associations a
                    JOIN social_graph.objects o ON o.id = a.id2
                    WHERE a.atype = {GraphAssociationType.Authored}
                      AND a.id1 = ANY({authorIds})
                      AND o.otype = {objectType}
                ) ranked
                WHERE rn <= {perAuthor}
                """)
            .ToListAsync(cancellationToken);
    }

    private bool IsInMemory() => string.Equals(
        _dbContext.Database.ProviderName,
        "Microsoft.EntityFrameworkCore.InMemory",
        StringComparison.Ordinal);

    private async Task AddRecentCandidatesAsync(
        Dictionary<long, CandidateItemResult> candidates,
        short objectType,
        string source,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => item.otype == objectType)
            .OrderByDescending(item => item.id)
            .Take(limit * 3)
            .ToListAsync(cancellationToken);

        var eligible = rows
            .Where(row => objectType is not (GraphObjectType.FeedPost or GraphObjectType.Reel) ||
                          GraphJson.Int(GraphJson.ParseObject(row.data), "privacy") == 0)
            .ToList();
        if (eligible.Count == 0)
        {
            return;
        }

        // One lookup for the whole page. This used to resolve the author inside the loop, so
        // a single feed request issued up to limit * 3 sequential round trips — 1500 for a
        // 500-item page — against a database on the far side of a tailnet.
        var authorByObject = await GetAuthorIdsAsync(
            eligible.Select(row => row.id).ToArray(),
            cancellationToken);

        foreach (var row in eligible)
        {
            AddCandidate(
                candidates,
                row.id,
                authorByObject.TryGetValue(row.id, out var authorId) ? authorId : 0,
                row.data,
                source,
                blocked);
        }
    }

    /// <summary>Resolves the author of many objects in a single query.</summary>
    private async Task<IReadOnlyDictionary<long, long>> GetAuthorIdsAsync(
        IReadOnlyCollection<long> objectIds,
        CancellationToken cancellationToken)
    {
        if (objectIds.Count == 0)
        {
            return new Dictionary<long, long>();
        }

        var ids = objectIds.Distinct().ToArray();
        var links = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.atype == GraphAssociationType.AuthoredBy && ids.Contains(item.id1))
            .Select(item => new { item.id1, item.id2 })
            .ToListAsync(cancellationToken);

        return links
            .GroupBy(link => link.id1)
            .ToDictionary(group => group.Key, group => group.First().id2);
    }

    private static void AddCandidate(
        Dictionary<long, CandidateItemResult> candidates,
        long objectId,
        long authorId,
        string dataJson,
        string source,
        ISet<long> blocked)
    {
        if (authorId <= 0 || blocked.Contains(authorId) || candidates.ContainsKey(objectId))
        {
            return;
        }

        var data = GraphJson.ParseObject(dataJson);
        candidates[objectId] = new CandidateItemResult(
            objectId,
            authorId,
            source,
            GraphJson.String(data, "create"));
    }

    private async Task<IReadOnlyList<long>> GetAssociationIdsAsync(long id1, short atype, int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, 1000);
        return await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == id1 && item.atype == atype)
            .OrderByDescending(item => item.time)
            .ThenByDescending(item => item.id2)
            .Take(take)
            .Select(item => item.id2)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<long>> GetUserGroupIdsAsync(long userId, CancellationToken cancellationToken)
    {
        var memberGroups = await GetAssociationIdsAsync(userId, GraphAssociationType.Member, 200, cancellationToken);
        var adminGroups = await GetAssociationIdsAsync(userId, GraphAssociationType.Admin, 200, cancellationToken);
        return memberGroups.Concat(adminGroups).Distinct().ToArray();
    }

    private async Task<ISet<long>> GetBlockedUserIdsAsync(long userId, CancellationToken cancellationToken)
    {
        return (await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId &&
                (item.atype == GraphAssociationType.Blocked || item.atype == GraphAssociationType.BlockedBy))
            .Select(item => item.id2)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    private async Task<long> GetAuthorIdAsync(long objectId, CancellationToken cancellationToken)
    {
        var author = await _associationService.RetrieveAssociationAsync(objectId, GraphAssociationType.AuthoredBy, null, 1, cancellationToken);
        return author.items.FirstOrDefault()?.id2 ?? 0;
    }

}
