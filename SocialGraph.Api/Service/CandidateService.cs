namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class CandidateService : ICandidateService
{
    private readonly MyDbContext _dbContext;
    private readonly IAssociationService _associationService;
    private readonly IBillingClient _billingClient;

    public CandidateService(
        MyDbContext dbContext,
        IAssociationService associationService,
        IBillingClient billingClient)
    {
        _dbContext = dbContext;
        _associationService = associationService;
        _billingClient = billingClient;
    }

    public async Task<IReadOnlyList<CandidateItemResult>> GetPostCandidatesAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 500);
        var blocked = await GetBlockedUserIdsAsync(userId, cancellationToken);
        var candidates = new Dictionary<long, CandidateItemResult>();

        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Friend, 200, cancellationToken), GraphObjectType.Post, "friend", blocked, take, cancellationToken);
        await AddAuthorCandidatesAsync(candidates, await GetAssociationIdsAsync(userId, GraphAssociationType.Followed, 200, cancellationToken), GraphObjectType.Post, "followed", blocked, take, cancellationToken);
        await AddGroupPostCandidatesAsync(candidates, await GetUserGroupIdsAsync(userId, cancellationToken), blocked, take, cancellationToken);
        await AddRecentCandidatesAsync(candidates, GraphObjectType.Post, "recent_public", blocked, take, cancellationToken);

        return candidates.Values
            .OrderByDescending(item => item.BoostMultiplier)
            .ThenByDescending(item => item.Id)
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
            .OrderByDescending(item => item.BoostMultiplier)
            .ThenByDescending(item => item.Id)
            .Take(take)
            .ToArray();
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
                await AddCandidateAsync(candidates, row.id, row.AuthorId, row.data, source, blocked, cancellationToken);
            }
        }
    }

    private async Task AddGroupPostCandidatesAsync(
        Dictionary<long, CandidateItemResult> candidates,
        IReadOnlyList<long> groupIds,
        ISet<long> blocked,
        int limit,
        CancellationToken cancellationToken)
    {
        foreach (var groupId in groupIds)
        {
            var rows = await (
                from association in _dbContext.AssociationsTb.AsNoTracking()
                join obj in _dbContext.ObjectsTb.AsNoTracking() on association.id2 equals obj.id
                where association.id1 == groupId &&
                    association.atype == GraphAssociationType.Published &&
                    obj.otype == GraphObjectType.Post
                orderby association.time descending
                select new { obj.id, obj.data })
                .Take(Math.Max(5, limit / 2))
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                var authorId = await GetAuthorIdAsync(row.id, cancellationToken);
                await AddCandidateAsync(candidates, row.id, authorId, row.data, "group", blocked, cancellationToken);
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
            if (GraphJson.Int(data, "privacy") != 0)
            {
                continue;
            }

            var authorId = await GetAuthorIdAsync(row.id, cancellationToken);
            await AddCandidateAsync(candidates, row.id, authorId, row.data, source, blocked, cancellationToken);
        }
    }

    private async Task AddCandidateAsync(
        Dictionary<long, CandidateItemResult> candidates,
        long objectId,
        long authorId,
        string dataJson,
        string source,
        ISet<long> blocked,
        CancellationToken cancellationToken)
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
            GraphJson.String(data, "create"),
            await _billingClient.GetFeedBoostMultiplierAsync(authorId, cancellationToken));
    }

    private async Task<IReadOnlyList<long>> GetAssociationIdsAsync(long id1, short atype, int limit, CancellationToken cancellationToken)
    {
        var page = await _associationService.RetrieveAssociationAsync(id1, atype, null, limit, cancellationToken);
        return page.items.Select(item => item.id2).ToArray();
    }

    private async Task<IReadOnlyList<long>> GetUserGroupIdsAsync(long userId, CancellationToken cancellationToken)
    {
        var memberGroups = await GetAssociationIdsAsync(userId, GraphAssociationType.Member, 200, cancellationToken);
        var adminGroups = await GetAssociationIdsAsync(userId, GraphAssociationType.Admin, 200, cancellationToken);
        return memberGroups.Concat(adminGroups).Distinct().ToArray();
    }

    private async Task<ISet<long>> GetBlockedUserIdsAsync(long userId, CancellationToken cancellationToken)
    {
        var blocked = await GetAssociationIdsAsync(userId, GraphAssociationType.Blocked, 500, cancellationToken);
        var blockedBy = await GetAssociationIdsAsync(userId, GraphAssociationType.BlockedBy, 500, cancellationToken);
        return blocked.Concat(blockedBy).ToHashSet();
    }

    private async Task<long> GetAuthorIdAsync(long objectId, CancellationToken cancellationToken)
    {
        var author = await _associationService.RetrieveAssociationAsync(objectId, GraphAssociationType.AuthoredBy, null, 1, cancellationToken);
        return author.items.FirstOrDefault()?.id2 ?? 0;
    }
}
