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

        await AddAuthoredPostIdsAsync(candidates, friends, blocked, take, includePrivate: true, cancellationToken);
        await AddAuthoredPostIdsAsync(candidates, followed, blocked, take, includePrivate: false, cancellationToken);
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

        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 200, cancellationToken), GraphObjectType.Reel, "friend", blocked, take, cancellationToken);
        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 200, cancellationToken), GraphObjectType.Reel, "followed", blocked, take, cancellationToken);
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
        bool includePrivate,
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
                post.otype == GraphObjectType.FeedPost
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

            if (!includePrivate && GraphJson.Int(GraphJson.ParseObject(row.data), "privacy") != 0)
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
            where post.otype == GraphObjectType.FeedPost &&
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
        CancellationToken cancellationToken)
    {
        foreach (var authorId in authorIds.Where(id => !blocked.Contains(id)))
        {
            var rows = await (
                from association in _dbContext.AssociationsTb.AsNoTracking()
                join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
                where association.id1 == authorId &&
                    association.atype == GraphAssociationType.Authored &&
                    obj.otype == objectType
                orderby association.time descending
                select new { obj.id, obj.data, AuthorId = authorId })
                .Take(Math.Max(5, limit / 2))
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                AddCandidate(candidates, row.id, row.AuthorId, row.data, source, blocked);
            }
        }
    }

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

        foreach (var row in rows)
        {
            var data = GraphJson.ParseObject(row.data);
            if (objectType == GraphObjectType.FeedPost && GraphJson.Int(data, "privacy") != 0)
            {
                continue;
            }

            var authorId = await GetAuthorIdAsync(row.id, cancellationToken);
            AddCandidate(candidates, row.id, authorId, row.data, source, blocked);
        }
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
