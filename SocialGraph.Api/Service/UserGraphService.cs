namespace SocialGraph.Api.Service;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class UserGraphService : IUserGraphService
{
    private const string AvatarPhotoActivityContent = "đã cập nhật ảnh đại diện";
    private const string CoverPhotoActivityContent = "tôi đã cập nhật ảnh bìa của mình";
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;
    private readonly MyDbContext? _dbContext;
    private readonly IContentGraphService? _contentGraphService;
    private readonly IMediaOwnershipGuard? _mediaOwnershipGuard;

    public UserGraphService(
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient,
        MyDbContext? dbContext = null,
        IContentGraphService? contentGraphService = null,
        IMediaOwnershipGuard? mediaOwnershipGuard = null)
    {
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
        _dbContext = dbContext;
        _contentGraphService = contentGraphService;
        _mediaOwnershipGuard = mediaOwnershipGuard;
    }

    /// <summary>
    /// Refuses client-supplied media URLs that <paramref name="ownerUserId"/> does not own.
    /// Stored URLs drive permanent deletion, so accepting a foreign URL here would let any
    /// user destroy another user's media by replacing their own avatar twice.
    /// </summary>
    private Task EnsureMediaOwnedAsync(long ownerUserId, IEnumerable<string?> urls, CancellationToken cancellationToken) =>
        _mediaOwnershipGuard is null
            ? Task.CompletedTask
            : _mediaOwnershipGuard.EnsureOwnedAsync(ownerUserId, urls, cancellationToken);

    public async Task<CreateUserPayload> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default)
    {
        SocialGraphObjectResult? user = null;
        await using var transaction = await BeginTransactionAsync(cancellationToken);

        try
        {
            user = await _objectService.AddObjectAsync(
                GraphObjectType.User,
                GraphJson.UserJson(input.Name, input.Gender, input.Birthdate, input.Location),
                cancellationToken);
            await _externalServiceClient.CreateUserAsync(
                user.id,
                input.Email,
                input.Password,
                input.Name,
                input.Birthdate,
                input.Gender,
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new CreateUserPayload(true, user.id, "User created; downstream provisioning queued.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackCreateAsync(transaction, user, transactional: transaction is not null);
            throw;
        }
        catch (Exception)
        {
            await RollbackCreateAsync(transaction, user, transactional: transaction is not null);
            return new CreateUserPayload(false, null, "User creation could not be queued safely.");
        }
    }

    public async Task<UserProfileResult?> UpdateUserAsync(UpdateUserInput input, CancellationToken cancellationToken = default)
    {
        if (input.Privacy is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input.Privacy), "User privacy must be 0 (normal) or 1 (advanced).");
        }

        await EnsureMediaOwnedAsync(input.Id, new[] { input.Avatar, input.Background }, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var patchData = GraphJson.ParseObject(GraphJson.PatchJson(
            ("avatar", input.Avatar),
            ("background", input.Background),
            ("name", input.Name),
            ("bio", input.Bio),
            ("gender", input.Gender is null ? null : input.Gender.Value ? 1 : 0),
            ("birthdate", input.Birthdate),
            ("location", input.Location),
            ("privacy", input.Privacy)));
        if (input.Avatar is not null)
        {
            // The generic profile mutation cannot assert which post/media produced a URL.
            // Clear stale provenance instead of letting it point at a different avatar.
            patchData["avatarSource"] = null;
        }
        var patch = patchData.ToJsonString();

        try
        {
            if (input.Privacy is not null)
            {
                // Serialize an account-mode change with new follow attempts. Without this,
                // a follower could pass the advanced-mode check immediately before a
                // downgrade removes existing followers, then insert a new edge afterwards.
                await AcquireFollowPolicyLockAsync(input.Id, cancellationToken);
            }

            var updated = await _objectService.UpdateObjectAsync(input.Id, GraphObjectType.User, patch, cancellationToken);
            if (updated is null)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return null;
            }

            // Normal profiles support friendships only. Removing the complete incoming
            // follower bucket also removes every inverse Followed edge, so an old follower
            // cannot retain access after the account leaves advanced mode.
            if (input.Privacy == 0)
            {
                await _associationService.DeleteAllAssociationAsync(
                    input.Id,
                    GraphAssociationType.FollowedBy,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(input.Name))
            {
                await _externalServiceClient.UpdateSearchIndexAsync(input.Id, "user", input.Name, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, input.Id);
            throw;
        }

        return await GetProfileAsync(input.Id, cancellationToken);
    }

    /// <summary>Maximum authored items removed per transaction when deleting a user.</summary>
    private const int DeleteContentBatchSize = 100;

    public async Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var current = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        var currentData = current is null ? null : GraphJson.ParseObject(current.data);
        var profileMedia = currentData is null
            ? Array.Empty<string>()
            : new[]
            {
                GraphJson.String(currentData, "avatar"),
                GraphJson.String(currentData, "background")
            }.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Authored content is removed in bounded batches, each committing on its own.
        // Previously the whole deletion ran in a single transaction: one iteration per item
        // the user had ever written, each cascading into association and orphan-media
        // cleanup. For an account with thousands of posts that transaction ran for minutes
        // while holding locks on objects and associations — the two tables every other
        // request needs. Deleting the user object stays transactional below.
        //
        // Partial progress is now possible if a batch fails, which is safe: the pass is
        // idempotent and re-running the mutation picks up whatever is left.
        await DeleteAuthoredContentAsync(userId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            await _associationService.DeleteObjectAssociationsAsync(userId, cancellationToken);
            var deleted = await _objectService.DeleteObjectAsync(userId, cancellationToken);
            if (deleted)
            {
                await _externalServiceClient.DeleteUserAsync(userId, cancellationToken);
                // Cascade cleanup of stored profile artwork; ownership was validated when it was set.
                await _externalServiceClient.DeleteMediaAsync(profileMedia, null, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return deleted;
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, userId);
            throw;
        }
    }

    public async Task<UserProfileResult?> GetProfileAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (_dbContext is not null)
        {
            return (await GetProfilesFromDatabaseAsync(new[] { userId }, null, cancellationToken)).FirstOrDefault();
        }

        return await GetProfileFromServicesAsync(userId, cancellationToken);
    }

    public async Task<ProfileAvatarSourceResult?> GetAvatarSourceAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return null;
        }

        var user = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        return user is null || user.otype != GraphObjectType.User
            ? null
            : ReadAvatarSource(GraphJson.ParseObject(user.data));
    }

    public async Task<IReadOnlyList<UserProfileResult>> GetProfilesForViewerAsync(
        long viewerId,
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken = default)
    {
        var requestedIds = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (requestedIds.Length == 0)
        {
            return Array.Empty<UserProfileResult>();
        }

        if (_dbContext is not null)
        {
            return await GetProfilesFromDatabaseAsync(requestedIds, viewerId, cancellationToken);
        }

        var profiles = new List<UserProfileResult>(requestedIds.Length);
        foreach (var userId in requestedIds)
        {
            if (viewerId != userId &&
                (await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken) ||
                 await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken)))
            {
                continue;
            }

            var profile = await GetProfileFromServicesAsync(userId, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    public async Task<IReadOnlyList<long>> GetFriendIdsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        return await GetProfileConnectionIdsAsync(userId, GraphAssociationType.Friend, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetProfileConnectionIdsAsync(
        long userId,
        short associationType,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
        if (associationType is not (GraphAssociationType.Friend or GraphAssociationType.Followed or GraphAssociationType.FollowedBy))
        {
            throw new ArgumentOutOfRangeException(nameof(associationType));
        }

        if (_dbContext is not null)
        {
            var viewerExists = await _dbContext.ObjectsTb
                .AsNoTracking()
                .AnyAsync(item => item.id == userId && item.otype == GraphObjectType.User, cancellationToken);
            if (!viewerExists)
            {
                return Array.Empty<long>();
            }

            var blockedIds = _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId &&
                               (item.atype == GraphAssociationType.Blocked ||
                                item.atype == GraphAssociationType.BlockedBy))
                .Select(item => item.id2);

            return await (
                    from relation in _dbContext.AssociationsTb.AsNoTracking()
                    join candidate in _dbContext.ObjectsTb.AsNoTracking()
                        on relation.id2 equals candidate.id
                    where relation.id1 == userId &&
                          relation.atype == associationType &&
                          relation.id2 != userId &&
                          candidate.otype == GraphObjectType.User &&
                          !blockedIds.Contains(relation.id2)
                    select relation.id2)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        }

        var ids = new List<long>();
        string? cursor = null;
        do
        {
            var page = await _associationService.RetrieveAssociationAsync(
                userId,
                associationType,
                cursor,
                100,
                cancellationToken);
            ids.AddRange(page.items.Select(item => item.id2).Where(id => id > 0 && id != userId));
            cursor = page.nextCursor;
        }
        while (cursor is not null);

        return ids.Distinct().OrderBy(id => id).ToArray();
    }

    public async Task<IReadOnlyList<UserProfileResult>> GetFriendRelationProfilesAsync(
        long userId,
        short associationType,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (associationType is not (GraphAssociationType.Friend or
            GraphAssociationType.FriendRequest or
            GraphAssociationType.HaveFriendRequest or
            GraphAssociationType.Blocked))
        {
            throw new ArgumentOutOfRangeException(
                nameof(associationType),
                associationType,
                "Only friend, incoming request, outgoing request and blocked relations are supported.");
        }

        var take = Math.Clamp(limit, 1, 100);
        if (_dbContext is not null)
        {
            var relationIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId && item.atype == associationType)
                .OrderByDescending(item => item.time)
                .ThenByDescending(item => item.id2)
                .Select(item => item.id2)
                .Take(take)
                .ToArrayAsync(cancellationToken);
            if (relationIds.Length == 0)
            {
                return Array.Empty<UserProfileResult>();
            }

            // A user must still be able to see the people in their own block list so they can unblock them.
            long? profileViewerId = associationType == GraphAssociationType.Blocked ? null : userId;
            return await GetProfilesFromDatabaseAsync(relationIds, profileViewerId, cancellationToken);
        }

        var page = await _associationService.RetrieveAssociationAsync(
            userId,
            associationType,
            null,
            take,
            cancellationToken);
        var profiles = new List<UserProfileResult>(page.items.Count);
        foreach (var relation in page.items)
        {
            var profile = await GetProfileFromServicesAsync(relation.id2, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    public async Task<IReadOnlyList<FriendProfileWithMutualCountResult>> GetFriendProfilesWithMutualCountsAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var profiles = await GetFriendRelationProfilesAsync(
            userId,
            GraphAssociationType.Friend,
            limit,
            cancellationToken);
        if (profiles.Count == 0)
        {
            return Array.Empty<FriendProfileWithMutualCountResult>();
        }

        var mutualCounts = await GetMutualFriendCountsAsync(
            userId,
            profiles.Select(profile => profile.Id).ToArray(),
            cancellationToken);

        return profiles
            .Select(profile => new FriendProfileWithMutualCountResult(
                profile,
                mutualCounts.GetValueOrDefault(profile.Id)))
            .ToArray();
    }

    public async Task<IReadOnlyList<FriendProfileWithMutualCountResult>> GetProfileConnectionsAsync(
        long userId,
        short associationType,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (associationType is not (GraphAssociationType.Friend or GraphAssociationType.Followed or GraphAssociationType.FollowedBy))
        {
            throw new ArgumentOutOfRangeException(nameof(associationType));
        }

        var take = Math.Clamp(limit, 1, 200);
        IReadOnlyList<UserProfileResult> profiles;
        if (_dbContext is not null)
        {
            var relationIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId && item.atype == associationType)
                .OrderByDescending(item => item.time)
                .ThenByDescending(item => item.id2)
                .Select(item => item.id2)
                .Take(1000)
                .ToArrayAsync(cancellationToken);
            profiles = relationIds.Length == 0
                ? Array.Empty<UserProfileResult>()
                : await GetProfilesFromDatabaseAsync(relationIds, userId, cancellationToken);
        }
        else
        {
            var relationIds = new List<long>();
            string? cursor = null;
            do
            {
                var page = await _associationService.RetrieveAssociationAsync(
                    userId,
                    associationType,
                    cursor,
                    100,
                    cancellationToken);
                relationIds.AddRange(page.items.Select(item => item.id2));
                cursor = page.nextCursor;
            }
            while (cursor is not null && relationIds.Count < 1000);

            var hydrated = new List<UserProfileResult>();
            foreach (var relationId in relationIds.Distinct())
            {
                var profile = await GetProfileFromServicesAsync(relationId, cancellationToken);
                if (profile is not null)
                {
                    hydrated.Add(profile);
                }
            }
            profiles = hydrated;
        }

        var selected = profiles
            .Take(take)
            .ToArray();
        var mutualCounts = associationType == GraphAssociationType.Friend
            ? await GetMutualFriendCountsAsync(userId, selected.Select(profile => profile.Id).ToArray(), cancellationToken)
            : new Dictionary<long, int>();
        return selected
            .Select(profile => new FriendProfileWithMutualCountResult(profile, mutualCounts.GetValueOrDefault(profile.Id)))
            .ToArray();
    }

    public async Task<IReadOnlyList<FriendProfileWithMutualCountResult>> GetProfileFriendsForViewerAsync(
        long targetUserId,
        long viewerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (targetUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetUserId));
        }
        if (viewerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewerId));
        }

        var take = Math.Clamp(limit, 1, 200);
        IReadOnlyList<long> relationIds;
        if (_dbContext is not null)
        {
            relationIds = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == targetUserId && item.atype == GraphAssociationType.Friend)
                .OrderByDescending(item => item.time)
                .ThenByDescending(item => item.id2)
                .Select(item => item.id2)
                .Take(1000)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var ids = new List<long>();
            string? cursor = null;
            do
            {
                var page = await _associationService.RetrieveAssociationAsync(
                    targetUserId,
                    GraphAssociationType.Friend,
                    cursor,
                    100,
                    cancellationToken);
                ids.AddRange(page.items.Select(item => item.id2));
                cursor = page.nextCursor;
            }
            while (cursor is not null && ids.Count < 1000);
            relationIds = ids;
        }

        if (relationIds.Count == 0)
        {
            return Array.Empty<FriendProfileWithMutualCountResult>();
        }

        var visibleProfiles = await GetProfilesForViewerAsync(
            viewerId,
            relationIds.Where(id => id > 0 && id != targetUserId).Distinct().ToArray(),
            cancellationToken);
        var selected = visibleProfiles.Take(take).ToArray();
        var mutualCounts = await GetMutualFriendCountsAsync(
            viewerId,
            selected.Select(profile => profile.Id).ToArray(),
            cancellationToken);
        return selected
            .Select(profile => new FriendProfileWithMutualCountResult(profile, mutualCounts.GetValueOrDefault(profile.Id)))
            .ToArray();
    }

    private async Task<Dictionary<long, int>> GetMutualFriendCountsAsync(
        long userId,
        IReadOnlyCollection<long> candidateIds,
        CancellationToken cancellationToken)
    {
        if (candidateIds.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var viewerFriendIds = (await GetFriendIdsAsync(userId, cancellationToken)).ToHashSet();
        var mutualCounts = new Dictionary<long, int>();
        if (viewerFriendIds.Count > 0 && _dbContext is not null)
        {
            var ids = candidateIds.Distinct().ToArray();
            var mutualEdges = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => ids.Contains(item.id1) &&
                               item.atype == GraphAssociationType.Friend &&
                               viewerFriendIds.Contains(item.id2))
                .Select(item => new { CandidateId = item.id1, MutualFriendId = item.id2 })
                .Distinct()
                .ToListAsync(cancellationToken);
            return mutualEdges
                .GroupBy(edge => edge.CandidateId)
                .ToDictionary(group => group.Key, group => group.Count());
        }

        if (viewerFriendIds.Count == 0)
        {
            return mutualCounts;
        }

        foreach (var candidateId in candidateIds.Distinct())
        {
            var mutualIds = new HashSet<long>();
            string? cursor = null;
            do
            {
                var page = await _associationService.RetrieveAssociationAsync(
                    candidateId,
                    GraphAssociationType.Friend,
                    cursor,
                    100,
                    cancellationToken);
                foreach (var relation in page.items)
                {
                    if (viewerFriendIds.Contains(relation.id2))
                    {
                        mutualIds.Add(relation.id2);
                    }
                }
                cursor = page.nextCursor;
            }
            while (cursor is not null);
            mutualCounts[candidateId] = mutualIds.Count;
        }
        return mutualCounts;
    }

    public async Task<IReadOnlyList<FriendSuggestionResult>> GetFriendSuggestionsAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_dbContext is null)
        {
            return Array.Empty<FriendSuggestionResult>();
        }

        var take = Math.Clamp(limit, 1, 50);
        var excludedAssociationTypes = new short[]
        {
            GraphAssociationType.Friend,
            GraphAssociationType.FriendRequest,
            GraphAssociationType.HaveFriendRequest,
            GraphAssociationType.Blocked,
            GraphAssociationType.BlockedBy
        };
        var viewerEdges = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId && excludedAssociationTypes.Contains(item.atype))
            .Select(item => new { item.atype, item.id2 })
            .ToListAsync(cancellationToken);
        var directFriendIds = viewerEdges
            .Where(item => item.atype == GraphAssociationType.Friend)
            .Select(item => item.id2)
            .Distinct()
            .ToArray();
        var excludedIds = viewerEdges
            .Select(item => item.id2)
            .Append(userId)
            .Distinct()
            .ToArray();

        var mutualFriendIdsByCandidate = new Dictionary<long, List<long>>();
        if (directFriendIds.Length > 0)
        {
            var mutualEdges = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => directFriendIds.Contains(item.id1) &&
                               item.atype == GraphAssociationType.Friend &&
                               !excludedIds.Contains(item.id2))
                .Select(item => new { FriendId = item.id1, CandidateId = item.id2 })
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var edge in mutualEdges)
            {
                if (!mutualFriendIdsByCandidate.TryGetValue(edge.CandidateId, out var mutualIds))
                {
                    mutualIds = new List<long>();
                    mutualFriendIdsByCandidate[edge.CandidateId] = mutualIds;
                }
                mutualIds.Add(edge.FriendId);
            }
        }

        var selectedIds = mutualFriendIdsByCandidate
            .OrderByDescending(item => item.Value.Count)
            .ThenByDescending(item => item.Key)
            .Select(item => item.Key)
            .Take(take)
            .ToList();
        if (selectedIds.Count < take)
        {
            var remaining = take - selectedIds.Count;
            var alreadySelected = selectedIds.ToArray();
            var fallbackIds = await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => item.otype == GraphObjectType.User &&
                               !excludedIds.Contains(item.id) &&
                               !alreadySelected.Contains(item.id))
                .OrderByDescending(item => item.id)
                .Select(item => item.id)
                .Take(remaining)
                .ToListAsync(cancellationToken);
            selectedIds.AddRange(fallbackIds);
        }

        if (selectedIds.Count == 0)
        {
            return Array.Empty<FriendSuggestionResult>();
        }

        var profiles = await GetProfilesFromDatabaseAsync(selectedIds, userId, cancellationToken);
        var profilesById = profiles.ToDictionary(item => item.Id);
        var displayedMutualIds = selectedIds
            .SelectMany(candidateId => mutualFriendIdsByCandidate.TryGetValue(candidateId, out var ids) ? ids.Take(3) : Array.Empty<long>())
            .Distinct()
            .ToArray();
        var mutualProfiles = displayedMutualIds.Length == 0
            ? Array.Empty<UserProfileResult>()
            : (await GetProfilesFromDatabaseAsync(displayedMutualIds, userId, cancellationToken)).ToArray();
        var mutualProfilesById = mutualProfiles.ToDictionary(item => item.Id);

        return selectedIds
            .Where(profilesById.ContainsKey)
            .Select(candidateId =>
            {
                var mutualIds = mutualFriendIdsByCandidate.TryGetValue(candidateId, out var ids)
                    ? ids.Distinct().ToArray()
                    : Array.Empty<long>();
                var mutualFriends = mutualIds
                    .Take(3)
                    .Where(mutualProfilesById.ContainsKey)
                    .Select(mutualId => mutualProfilesById[mutualId])
                    .Select(profile => new UserSummaryResult(profile.Id, profile.Name, profile.Avatar, profile.IsVerified))
                    .ToArray();
                return new FriendSuggestionResult(profilesById[candidateId], mutualIds.Length, mutualFriends);
            })
            .ToArray();
    }

    private async Task<UserProfileResult?> GetProfileFromServicesAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var item = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (item is null || item.otype != GraphObjectType.User)
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        return new UserProfileResult(
            item.id,
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "gender"),
            GraphJson.String(data, "birthdate"),
            GraphJson.String(data, "location"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            GraphJson.NullableString(data, "verify"),
            IsVerifyActive(data),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.Friend, cancellationToken),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.FollowedBy, cancellationToken),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.Followed, cancellationToken));
    }

    private async Task<IReadOnlyList<UserProfileResult>> GetProfilesFromDatabaseAsync(
        IReadOnlyCollection<long> requestedIds,
        long? viewerId,
        CancellationToken cancellationToken)
    {
        var dbContext = _dbContext ?? throw new InvalidOperationException("A database context is required for batch profile reads.");
        var visibleIds = requestedIds.ToArray();
        if (viewerId is not null)
        {
            var blockedIds = await dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == viewerId.Value &&
                               visibleIds.Contains(item.id2) &&
                               (item.atype == GraphAssociationType.Blocked || item.atype == GraphAssociationType.BlockedBy))
                .Select(item => item.id2)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            if (blockedIds.Length > 0)
            {
                var blockedSet = blockedIds.ToHashSet();
                visibleIds = visibleIds
                    .Where(userId => userId == viewerId.Value || !blockedSet.Contains(userId))
                    .ToArray();
            }
        }

        if (visibleIds.Length == 0)
        {
            return Array.Empty<UserProfileResult>();
        }

        var users = await dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => visibleIds.Contains(item.id) && item.otype == GraphObjectType.User)
            .ToListAsync(cancellationToken);
        var relationTypes = new short[]
        {
            GraphAssociationType.Friend,
            GraphAssociationType.FollowedBy,
            GraphAssociationType.Followed
        };
        var countRows = await dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => visibleIds.Contains(item.id1) && relationTypes.Contains(item.atype))
            .GroupBy(item => new { item.id1, item.atype })
            .Select(group => new { group.Key.id1, group.Key.atype, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        var counts = countRows.ToDictionary(
            item => (item.id1, item.atype),
            item => item.Count);
        var usersById = users.ToDictionary(item => item.id);

        return visibleIds
            .Where(usersById.ContainsKey)
            .Select(userId => BuildProfile(usersById[userId], counts))
            .ToArray();
    }

    private static UserProfileResult BuildProfile(
        Objects item,
        IReadOnlyDictionary<(long UserId, short AssociationType), long> counts)
    {
        var data = GraphJson.ParseObject(item.data);
        return new UserProfileResult(
            item.id,
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "gender"),
            GraphJson.String(data, "birthdate"),
            GraphJson.String(data, "location"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            GraphJson.NullableString(data, "verify"),
            IsVerifyActive(data),
            ProfileCount(counts, item.id, GraphAssociationType.Friend),
            ProfileCount(counts, item.id, GraphAssociationType.FollowedBy),
            ProfileCount(counts, item.id, GraphAssociationType.Followed));
    }

    private static long ProfileCount(
        IReadOnlyDictionary<(long UserId, short AssociationType), long> counts,
        long userId,
        short associationType) =>
        counts.TryGetValue((userId, associationType), out var count) ? count : 0;

    public async Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl,
        int privacy,
        long? sourceContentId,
        long? sourceMediaId,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (currentUser is null || currentUser.otype != GraphObjectType.User)
        {
            return null;
        }

        if (privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(privacy), "Feed privacy must be between 0 and 3.");
        }

        var hasContentSource = sourceContentId.HasValue;
        var hasMediaSource = sourceMediaId.HasValue;
        if (hasContentSource != hasMediaSource ||
            hasContentSource && (sourceContentId <= 0 || sourceMediaId <= 0) ||
            hasContentSource && string.IsNullOrWhiteSpace(avatarUrl) ||
            hasContentSource && !string.IsNullOrWhiteSpace(originalUrl))
        {
            throw new ArgumentException("Avatar source is invalid.");
        }

        await EnsureMediaOwnedAsync(userId, new[] { avatarUrl, originalUrl }, cancellationToken);

        ProfileAvatarSourceResult? avatarSource = null;
        if (hasContentSource)
        {
            avatarSource = await ValidateAvatarSourceAsync(
                userId,
                sourceContentId!.Value,
                sourceMediaId!.Value,
                cancellationToken);
        }

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        long? createdActivityId = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(originalUrl))
            {
                if (_contentGraphService is null)
                {
                    throw new InvalidOperationException("Avatar activity service is unavailable.");
                }

                var activity = await _contentGraphService.CreateFeedPostAsync(
                    new CreateFeedPostInput(
                        userId,
                        AvatarPhotoActivityContent,
                        0,
                        new[] { new MediaInput(GraphMediaType.Photo, originalUrl) }),
                    cancellationToken);
                createdActivityId = activity.Id;
                var originalMedia = activity.Media.SingleOrDefault(media =>
                    media.Type == GraphMediaType.Photo &&
                    string.Equals(media.Url, originalUrl, StringComparison.OrdinalIgnoreCase));
                if (activity.Type != GraphObjectType.FeedPost ||
                    activity.AuthorId != userId ||
                    originalMedia is null)
                {
                    throw new InvalidOperationException("Avatar activity could not be created safely.");
                }

                avatarSource = new ProfileAvatarSourceResult(activity.Id, originalMedia.Id);
            }

            var currentData = GraphJson.ParseObject(currentUser.data);
            var previousUrl = GraphJson.String(currentData, "avatar");
            var updated = await _objectService.UpdateObjectAsync(
                userId,
                GraphObjectType.User,
                AvatarPatchJson(avatarUrl, avatarSource),
                cancellationToken);

            if (updated is null)
            {
                throw new InvalidOperationException("Profile avatar could not be updated safely.");
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                await _externalServiceClient.FinalizeMediaAsync(new[] { avatarUrl }, userId, cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(previousUrl) &&
                !string.Equals(previousUrl, avatarUrl, StringComparison.OrdinalIgnoreCase))
            {
                await _externalServiceClient.DeleteMediaAsync(new[] { previousUrl }, userId, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await GetProfileAsync(userId, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await _objectService.InvalidateObjectCacheAsync(userId);
                if (createdActivityId is { } activityId)
                {
                    await _objectService.InvalidateObjectCacheAsync(activityId);
                }
            }
            throw;
        }
    }

    public Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl = null,
        int privacy = 0,
        CancellationToken cancellationToken = default) =>
        ChangeUserAvatarAsync(userId, avatarUrl, originalUrl, privacy, null, null, cancellationToken);

    public Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl,
        CancellationToken cancellationToken) =>
        ChangeUserAvatarAsync(userId, avatarUrl, originalUrl, 0, null, null, cancellationToken);

    private async Task<ProfileAvatarSourceResult> ValidateAvatarSourceAsync(
        long userId,
        long contentId,
        long mediaId,
        CancellationToken cancellationToken)
    {
        if (_contentGraphService is null)
        {
            throw new InvalidOperationException("Avatar source validation is unavailable.");
        }

        var isAuthor = await _contentGraphService.IsAuthorAsync(userId, contentId, cancellationToken);
        var content = isAuthor
            ? await _contentGraphService.GetContentAsync(contentId, cancellationToken)
            : null;
        var media = content?.Media.SingleOrDefault(item => item.Id == mediaId);
        if (content is null ||
            content.Type != GraphObjectType.FeedPost ||
            content.AuthorId != userId ||
            !isAuthor ||
            media is null ||
            media.Type != GraphMediaType.Photo)
        {
            // Keep one public failure shape so probing arbitrary IDs reveals no ownership detail.
            throw new ArgumentException("Avatar source is invalid.");
        }

        return new ProfileAvatarSourceResult(contentId, mediaId);
    }

    private static string AvatarPatchJson(string avatarUrl, ProfileAvatarSourceResult? source)
    {
        var patch = new JsonObject
        {
            ["avatar"] = avatarUrl,
            ["avatarSource"] = source is null
                ? null
                : new JsonObject
                {
                    ["contentId"] = source.ContentId.ToString(CultureInfo.InvariantCulture),
                    ["mediaId"] = source.MediaId.ToString(CultureInfo.InvariantCulture)
                }
        };
        return patch.ToJsonString();
    }

    private static ProfileAvatarSourceResult? ReadAvatarSource(JsonObject data)
    {
        if (!data.TryGetPropertyValue("avatarSource", out var value) || value is not JsonObject source ||
            !TryReadAvatarSourceId(source, "contentId", out var contentId) ||
            !TryReadAvatarSourceId(source, "mediaId", out var mediaId))
        {
            return null;
        }

        return new ProfileAvatarSourceResult(contentId, mediaId);
    }

    private static bool TryReadAvatarSourceId(JsonObject source, string name, out long id)
    {
        id = 0;
        if (!source.TryGetPropertyValue(name, out var value) || value is null)
        {
            return false;
        }

        try
        {
            var text = value.GetValue<string?>();
            return text is { Length: > 0 and <= 19 } &&
                long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
                id > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public async Task<UserProfileResult?> SetUserVerifyAsync(
        long userId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
    {
        var verify = expiresAt?.ToUniversalTime().ToString("O");
        var updated = await _objectService.UpdateSystemObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJsonIncludingNulls(("verify", verify)),
            cancellationToken);

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public async Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl = null,
        int privacy = 0,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (currentUser is null || currentUser.otype != GraphObjectType.User)
        {
            return null;
        }

        if (privacy is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(privacy), "Feed privacy must be between 0 and 3.");
        }

        await EnsureMediaOwnedAsync(userId, new[] { backgroundUrl, originalUrl }, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var currentData = GraphJson.ParseObject(currentUser.data);
        var previousUrl = GraphJson.String(currentData, "background");
        var updated = await _objectService.UpdateObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJson(("background", backgroundUrl)),
            cancellationToken);

        if (updated is not null)
        {
            if (!string.IsNullOrWhiteSpace(backgroundUrl))
            {
                await _externalServiceClient.FinalizeMediaAsync(new[] { backgroundUrl }, userId, cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(previousUrl) &&
                !string.Equals(previousUrl, backgroundUrl, StringComparison.OrdinalIgnoreCase))
            {
                await _externalServiceClient.DeleteMediaAsync(new[] { previousUrl }, userId, cancellationToken);
            }
        }

        if (updated is not null && !string.IsNullOrWhiteSpace(originalUrl) && _contentGraphService is not null)
        {
            await _contentGraphService.CreateFeedPostAsync(
                new CreateFeedPostInput(
                    userId,
                    CoverPhotoActivityContent,
                    0,
                    new[] { new MediaInput(GraphMediaType.Photo, originalUrl) }),
                cancellationToken);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl,
        CancellationToken cancellationToken) =>
        ChangeUserBackgroundAsync(userId, backgroundUrl, originalUrl, 0, cancellationToken);

    public async Task<bool> SendFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(requesterId, receiverId, cancellationToken) ||
            await IsBlockedEitherWayAsync(requesterId, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.Friend, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.FriendRequest, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(receiverId, GraphAssociationType.FriendRequest, requesterId, cancellationToken))
        {
            return false;
        }

        await _associationService.AddAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
        await _externalServiceClient.NotifyAsync(
            requesterId,
            receiverId,
            ExternalNotificationAction.FriendRequest,
            requesterId,
            null,
            cancellationToken);
        return true;
    }

    public Task<bool> CancelFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
    }

    public async Task<bool> AcceptFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(requesterId, receiverId, cancellationToken) ||
            await IsBlockedEitherWayAsync(requesterId, receiverId, cancellationToken) ||
            !await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.FriendRequest, receiverId, cancellationToken))
        {
            return false;
        }

        var result = await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(requesterId, GraphAssociationType.FriendRequest, receiverId, false),
                new(requesterId, GraphAssociationType.Followed, receiverId, false),
                new(receiverId, GraphAssociationType.Followed, requesterId, false),
                new(requesterId, GraphAssociationType.Friend, receiverId, true)
            },
            cancellationToken);
        if (result)
        {
            await _externalServiceClient.NotifyAsync(
                receiverId,
                requesterId,
                ExternalNotificationAction.FriendAccept,
                receiverId,
                null,
                cancellationToken);
        }

        return result;
    }

    public Task<bool> RejectFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
    }

    public Task<bool> UnfriendAsync(long userId, long friendId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            userId,
            GraphAssociationType.Friend,
            friendId,
            cancellationToken);
    }

    public async Task<bool> FollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            await AcquireFollowPolicyLockAsync(targetUserId, cancellationToken);
            if (!await CanCreateUserRelationshipAsync(
                    followerId,
                    targetUserId,
                    cancellationToken,
                    requireTargetFollowEnabled: true) ||
                await IsBlockedEitherWayAsync(followerId, targetUserId, cancellationToken) ||
                await _associationService.HasAssociationAsync(followerId, GraphAssociationType.Friend, targetUserId, cancellationToken) ||
                await _associationService.HasAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken))
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return false;
            }

            var followed = await _associationService.AddAssociationAsync(
                followerId,
                GraphAssociationType.Followed,
                targetUserId,
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return followed;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken);
    }

    public async Task<bool> BlockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(blockerId, blockedUserId, cancellationToken))
        {
            return false;
        }

        return await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(blockerId, GraphAssociationType.Friend, blockedUserId, false),
                new(blockerId, GraphAssociationType.FriendRequest, blockedUserId, false),
                new(blockedUserId, GraphAssociationType.FriendRequest, blockerId, false),
                new(blockerId, GraphAssociationType.Followed, blockedUserId, false),
                new(blockedUserId, GraphAssociationType.Followed, blockerId, false),
                new(blockerId, GraphAssociationType.Blocked, blockedUserId, true)
            },
            cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(blockerId, GraphAssociationType.Blocked, blockedUserId, cancellationToken);
    }

    private async Task<bool> CanCreateUserRelationshipAsync(
        long userId,
        long targetUserId,
        CancellationToken cancellationToken,
        bool requireTargetFollowEnabled = false)
    {
        if (userId <= 0 || targetUserId <= 0 || userId == targetUserId)
        {
            return false;
        }

        short? sourceType;
        short? targetType;
        string? targetData;
        if (_dbContext is not null)
        {
            // Read from PostgreSQL after the follow-policy lock instead of consulting the
            // object cache, which may still contain the target's previous account mode.
            var users = await _dbContext.ObjectsTb
                .AsNoTracking()
                .Where(item => item.id == userId || item.id == targetUserId)
                .Select(item => new { item.id, item.otype, item.data })
                .ToListAsync(cancellationToken);
            var source = users.SingleOrDefault(item => item.id == userId);
            var target = users.SingleOrDefault(item => item.id == targetUserId);
            sourceType = source?.otype;
            targetType = target?.otype;
            targetData = target?.data;
        }
        else
        {
            var source = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
            var target = await _objectService.RetrieveObjectAsync(targetUserId, cancellationToken);
            sourceType = source?.otype;
            targetType = target?.otype;
            targetData = target?.data;
        }

        if (sourceType != GraphObjectType.User || targetType != GraphObjectType.User)
        {
            return false;
        }

        return !requireTargetFollowEnabled ||
               GraphJson.Int(GraphJson.ParseObject(targetData!), "privacy") == 1;
    }

    private async Task AcquireFollowPolicyLockAsync(long targetUserId, CancellationToken cancellationToken)
    {
        if (_dbContext is null ||
            _dbContext.Database.CurrentTransaction is null ||
            !string.Equals(
                _dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return;
        }

        // Group lifecycle operations use positive Snowflake IDs as advisory-lock keys.
        // User follow-policy operations use their negative counterpart to keep both lock
        // namespaces independent without truncating a 64-bit Snowflake identifier.
        var lockKey = -targetUserId;
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({lockKey})",
            cancellationToken);
    }

    private async Task<bool> IsBlockedEitherWayAsync(
        long userId,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        return await _associationService.HasAssociationAsync(userId, GraphAssociationType.Blocked, targetUserId, cancellationToken) ||
               await _associationService.HasAssociationAsync(userId, GraphAssociationType.BlockedBy, targetUserId, cancellationToken);
    }

    private static bool IsVerifyActive(System.Text.Json.Nodes.JsonObject data)
    {
        var raw = GraphJson.String(data, "verify");
        return DateTimeOffset.TryParse(raw, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Removes everything the user authored, committing a bounded batch at a time.
    /// </summary>
    /// <remarks>
    /// Each batch re-reads the remaining authored ids rather than paging a snapshot, because
    /// deleting content removes the very rows a cursor would sit on. Progress is guaranteed:
    /// a batch that deletes nothing ends the loop, so a stubborn item cannot spin forever.
    /// </remarks>
    private async Task DeleteAuthoredContentAsync(long userId, CancellationToken cancellationToken)
    {
        if (_contentGraphService is null || _dbContext is null)
        {
            return;
        }

        while (true)
        {
            var batch = await _dbContext.AssociationsTb
                .AsNoTracking()
                .Where(item => item.id1 == userId && item.atype == GraphAssociationType.Authored)
                .Select(item => item.id2)
                .Distinct()
                .Take(DeleteContentBatchSize)
                .ToArrayAsync(cancellationToken);
            if (batch.Length == 0)
            {
                return;
            }

            await using var batchTransaction = await BeginTransactionAsync(cancellationToken);
            var removed = 0;
            try
            {
                foreach (var contentId in batch)
                {
                    if (await _contentGraphService.DeleteContentAsync(contentId, cancellationToken))
                    {
                        removed++;
                    }
                }

                if (batchTransaction is not null)
                {
                    await batchTransaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                if (batchTransaction is not null)
                {
                    await batchTransaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }

            if (removed == 0)
            {
                // Nothing in this batch could be deleted; stop rather than loop on it.
                return;
            }
        }
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext is null ||
            _dbContext.Database.CurrentTransaction is not null ||
            !_dbContext.Database.IsRelational())
        {
            return null;
        }

        return await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    private async Task RollbackCreateAsync(
        IDbContextTransaction? transaction,
        SocialGraphObjectResult? user,
        bool transactional)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        if (user is null)
        {
            return;
        }

        if (transactional)
        {
            await _objectService.InvalidateObjectCacheAsync(user.id);
        }
        else
        {
            await _objectService.DeleteObjectAsync(user.id, CancellationToken.None);
        }
    }

    private async Task RollbackAndInvalidateAsync(IDbContextTransaction? transaction, long objectId)
    {
        if (transaction is null)
        {
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
        await _objectService.InvalidateObjectCacheAsync(objectId);
    }
}
