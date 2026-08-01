namespace SocialGraph.Api.Service;

using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class GroupGraphService : IGroupGraphService
{
    private const int MaxSuggestionFriendSources = 1_000;
    private const int MaxSuggestionMembershipEdges = 10_000;
    private readonly MyDbContext _dbContext;
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;
    private readonly IBlockVisibilityService _blockVisibility;
    private readonly IUserGraphService _userGraphService;
    private readonly TimeProvider _timeProvider;
    private readonly IContentGraphService? _contentGraphService;
    private readonly IMediaOwnershipGuard? _mediaOwnershipGuard;

    public GroupGraphService(
        MyDbContext dbContext,
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient,
        IBlockVisibilityService blockVisibility,
        IUserGraphService userGraphService,
        TimeProvider timeProvider,
        IContentGraphService? contentGraphService = null,
        IMediaOwnershipGuard? mediaOwnershipGuard = null)
    {
        _dbContext = dbContext;
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
        _blockVisibility = blockVisibility;
        _userGraphService = userGraphService;
        _timeProvider = timeProvider;
        _contentGraphService = contentGraphService;
        _mediaOwnershipGuard = mediaOwnershipGuard;
    }

    /// <summary>
    /// Refuses group media URLs that the acting admin does not own, so group artwork cannot be
    /// pointed at another user's asset and then destroyed by replacing it.
    /// </summary>
    private Task EnsureMediaOwnedAsync(long actorId, IEnumerable<string?> urls, CancellationToken cancellationToken) =>
        _mediaOwnershipGuard is null
            ? Task.CompletedTask
            : _mediaOwnershipGuard.EnsureOwnedAsync(actorId, urls, cancellationToken);

    public async Task<GroupResult> CreateGroupAsync(CreateGroupInput input, CancellationToken cancellationToken = default)
    {
        ValidateGroupPrivacy(input.Privacy);

        var group = await _objectService.AddObjectAsync(
            GraphObjectType.Group,
            GraphJson.GroupJson(input.Name, input.Bio, input.Privacy, input.Avatar, input.Background),
            cancellationToken);

        await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(input.CreatorId, GraphAssociationType.Member, group.id, true),
                new(input.CreatorId, GraphAssociationType.Admin, group.id, true)
            },
            cancellationToken);
        await _externalServiceClient.CreateSearchIndexAsync(group.id, "group", input.Name, cancellationToken);
        return (await GetGroupAsync(group.id, cancellationToken))!;
    }

    public async Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, CancellationToken cancellationToken = default)
    {
        ValidateGroupPrivacy(input.Privacy);

        var updated = await _objectService.UpdateObjectAsync(
            input.Id,
            GraphObjectType.Group,
            GraphJson.PatchJson(("avatar", input.Avatar), ("background", input.Background), ("name", input.Name), ("bio", input.Bio), ("privacy", input.Privacy)),
            cancellationToken);

        if (updated is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Name))
        {
            await _externalServiceClient.UpdateSearchIndexAsync(input.Id, "group", input.Name, cancellationToken);
        }

        return await GetGroupAsync(input.Id, cancellationToken);
    }

    public async Task<bool> DeleteGroupAsync(long actorId, long groupId, CancellationToken cancellationToken = default)
    {
        if (actorId <= 0 || groupId <= 0)
        {
            return false;
        }

        if (_dbContext.Database.IsRelational() && _dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Group deletion must own its database transaction.");
        }

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        if (transaction is not null && string.Equals(
                _dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            // Serialize deletion with leave/demotion and retain a Serializable predicate
            // read over membership so a concurrent approval/add cannot make this decision stale.
            await _dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(@groupId)",
                new object[] { new NpgsqlParameter("groupId", groupId) },
                cancellationToken);
        }

        // Deletion is the terminal path for the final participant only. Read directly
        // from PostgreSQL rather than trusting a cached role/count projection.
        var actorRoles = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == actorId && item.id2 == groupId &&
                (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin))
            .Select(item => item.atype)
            .ToArrayAsync(cancellationToken);
        if (!actorRoles.Contains(GraphAssociationType.Member) ||
            !actorRoles.Contains(GraphAssociationType.Admin) ||
            await _dbContext.AssociationsTb.AsNoTracking().AnyAsync(item =>
                (item.id1 == groupId && item.id2 != actorId &&
                    (item.atype == GraphAssociationType.HaveMember || item.atype == GraphAssociationType.HaveAdmin)) ||
                (item.id2 == groupId && item.id1 != actorId &&
                    (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin)),
                cancellationToken))
        {
            return false;
        }

        var current = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (current?.otype != GraphObjectType.Group)
        {
            return false;
        }

        var currentData = current is null ? null : GraphJson.ParseObject(current.data);
        var profileMedia = currentData is null
            ? Array.Empty<string>()
            : new[]
            {
                GraphJson.String(currentData, "avatar"),
                GraphJson.String(currentData, "background")
            }.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var groupPostIds = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == groupId && item.atype == GraphAssociationType.Published)
            .Select(item => item.id2)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        await _associationService.DeleteObjectAssociationsAsync(groupId, cancellationToken);
        var deleted = await _objectService.DeleteObjectAsync(groupId, cancellationToken);
        if (!deleted)
        {
            return false;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // Do not hold the lifecycle transaction while dispatching cleanup work. The group
        // is already unavailable; bounded/idempotent cleanup can safely finish afterwards.
        if (_contentGraphService is not null)
        {
            foreach (var postId in groupPostIds)
            {
                await _contentGraphService.DeleteContentAsync(postId, cancellationToken);
            }
        }
        await _externalServiceClient.DeleteSearchIndexAsync(groupId, cancellationToken);
        // Cascade cleanup of stored group artwork; ownership was validated when it was set.
        await _externalServiceClient.DeleteMediaAsync(profileMedia, null, cancellationToken);
        return true;
    }

    public async Task<GroupResult?> GetGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (item is null || item.otype != GraphObjectType.Group)
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        return new GroupResult(
            item.id,
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            await _associationService.CountAssociationAsync(groupId, GraphAssociationType.HaveMember, cancellationToken),
            await _associationService.CountAssociationAsync(groupId, GraphAssociationType.HaveAdmin, cancellationToken));
    }

    public async Task<IReadOnlyList<GroupSuggestionResult>> GetGroupSuggestionsAsync(
        long userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 50);
        var friendIds = await (
                from edge in _dbContext.AssociationsTb.AsNoTracking()
                join friendObject in _dbContext.ObjectsTb.AsNoTracking()
                    on edge.id2 equals friendObject.id
                where edge.id1 == userId &&
                      edge.atype == GraphAssociationType.Friend &&
                      friendObject.otype == GraphObjectType.User
                orderby edge.time descending, edge.id2 descending
                select edge.id2)
            .Take(MaxSuggestionFriendSources)
            .ToArrayAsync(cancellationToken);
        if (friendIds.Length == 0)
        {
            return Array.Empty<GroupSuggestionResult>();
        }

        var blockedFriendIds = await _blockVisibility.GetBlockedUserIdsAsync(
            userId,
            friendIds,
            cancellationToken);
        var visibleFriendIds = friendIds
            .Where(friendId => !blockedFriendIds.Contains(friendId))
            .ToArray();
        if (visibleFriendIds.Length == 0)
        {
            return Array.Empty<GroupSuggestionResult>();
        }

        var excludedViewerAssociationTypes = new short[]
        {
            GraphAssociationType.Member,
            GraphAssociationType.Admin,
            GraphAssociationType.GroupJoinRequest
        };
        var candidateMembershipTypes = new short[]
        {
            GraphAssociationType.Member,
            GraphAssociationType.Admin
        };
        var candidateMemberships = _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(membership =>
                visibleFriendIds.Contains(membership.id1) &&
                candidateMembershipTypes.Contains(membership.atype))
            .OrderByDescending(membership => membership.time)
            .ThenByDescending(membership => membership.id2)
            .Take(MaxSuggestionMembershipEdges);
        var candidateIds = await (
                from membership in candidateMemberships
                join groupObject in _dbContext.ObjectsTb.AsNoTracking()
                    on membership.id2 equals groupObject.id
                where groupObject.otype == GraphObjectType.Group &&
                      !_dbContext.AssociationsTb.Any(viewerEdge =>
                          viewerEdge.id1 == userId &&
                          viewerEdge.id2 == groupObject.id &&
                          excludedViewerAssociationTypes.Contains(viewerEdge.atype))
                group membership by groupObject.id
                into candidate
                orderby candidate.Select(edge => edge.id1).Distinct().Count() descending,
                    candidate.Key descending
                select candidate.Key)
            .Take(take)
            .ToArrayAsync(cancellationToken);
        if (candidateIds.Length == 0)
        {
            return Array.Empty<GroupSuggestionResult>();
        }

        var groupObjects = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => candidateIds.Contains(item.id) && item.otype == GraphObjectType.Group)
            .ToDictionaryAsync(item => item.id, cancellationToken);
        var counts = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(edge => candidateIds.Contains(edge.id1) &&
                (edge.atype == GraphAssociationType.HaveMember ||
                 edge.atype == GraphAssociationType.HaveAdmin))
            .GroupBy(edge => new { edge.id1, edge.atype })
            .Select(group => new { group.Key.id1, group.Key.atype, Count = group.LongCount() })
            .ToArrayAsync(cancellationToken);
        var countsByGroup = counts.ToDictionary(
            item => (item.id1, item.atype),
            item => item.Count);

        var friendMembershipRows = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(edge => candidateIds.Contains(edge.id2) &&
                visibleFriendIds.Contains(edge.id1) &&
                candidateMembershipTypes.Contains(edge.atype))
            .Select(edge => new { GroupId = edge.id2, FriendId = edge.id1, edge.time })
            .ToArrayAsync(cancellationToken);
        var friendIdsByGroup = friendMembershipRows
            .GroupBy(row => row.GroupId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(row => row.FriendId)
                    .Select(rows => new { FriendId = rows.Key, LatestMembership = rows.Max(row => row.time) })
                    .OrderByDescending(row => row.LatestMembership)
                    .ThenByDescending(row => row.FriendId)
                    .Select(row => row.FriendId)
                    .ToArray());
        var previewFriendIds = friendIdsByGroup.Values
            .SelectMany(ids => ids.Take(3))
            .Distinct()
            .ToArray();
        var previewProfiles = previewFriendIds.Length == 0
            ? Array.Empty<UserProfileResult>()
            : (await _userGraphService.GetProfilesForViewerAsync(
                userId,
                previewFriendIds,
                cancellationToken)).ToArray();
        var previewProfilesById = previewProfiles.ToDictionary(profile => profile.Id);

        var now = _timeProvider.GetUtcNow();
        var todayStart = new DateTimeOffset(
            now.Year,
            now.Month,
            now.Day,
            0,
            0,
            0,
            TimeSpan.Zero);
        var yesterdayStartMilliseconds = todayStart.AddDays(-1).ToUnixTimeMilliseconds();
        var todayStartMilliseconds = todayStart.ToUnixTimeMilliseconds();
        var yesterdayPostCounts = await (
                from published in _dbContext.AssociationsTb.AsNoTracking()
                join postObject in _dbContext.ObjectsTb.AsNoTracking()
                    on published.id2 equals postObject.id
                where candidateIds.Contains(published.id1) &&
                      published.atype == GraphAssociationType.Published &&
                      published.time >= yesterdayStartMilliseconds &&
                      published.time < todayStartMilliseconds &&
                      postObject.otype == GraphObjectType.GroupPost
                group published by published.id1
                into posts
                select new { GroupId = posts.Key, Count = posts.LongCount() })
            .ToDictionaryAsync(item => item.GroupId, item => item.Count, cancellationToken);

        return candidateIds
            .Where(groupObjects.ContainsKey)
            .Select(groupId =>
            {
                var item = groupObjects[groupId];
                var data = GraphJson.ParseObject(item.data);
                var groupFriendIds = friendIdsByGroup.GetValueOrDefault(groupId) ?? Array.Empty<long>();
                var friendMembers = groupFriendIds
                    .Take(3)
                    .Where(previewProfilesById.ContainsKey)
                    .Select(friendId => previewProfilesById[friendId])
                    .Select(profile => new GroupSuggestionFriendResult(
                        profile.Id,
                        profile.Name,
                        profile.Avatar))
                    .ToArray();
                return new GroupSuggestionResult(
                    new GroupResult(
                        item.id,
                        GraphJson.String(data, "avatar"),
                        GraphJson.String(data, "background"),
                        GraphJson.String(data, "name"),
                        GraphJson.String(data, "bio"),
                        GraphJson.Int(data, "privacy"),
                        GraphJson.String(data, "create"),
                        countsByGroup.GetValueOrDefault((groupId, GraphAssociationType.HaveMember)),
                        countsByGroup.GetValueOrDefault((groupId, GraphAssociationType.HaveAdmin))),
                    groupFriendIds.Length,
                    friendMembers,
                    yesterdayPostCounts.GetValueOrDefault(groupId));
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<GroupSuggestionFriendResult>> GetGroupFriendMembersAsync(
        long viewerId,
        long groupId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 12);
        var groupExists = await _dbContext.ObjectsTb
            .AsNoTracking()
            .AnyAsync(item => item.id == groupId && item.otype == GraphObjectType.Group, cancellationToken);
        if (!groupExists)
        {
            return Array.Empty<GroupSuggestionFriendResult>();
        }

        var friendIds = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(friend =>
                friend.id1 == viewerId &&
                friend.atype == GraphAssociationType.Friend &&
                _dbContext.AssociationsTb.Any(membership =>
                    membership.id1 == friend.id2 &&
                    membership.id2 == groupId &&
                    (membership.atype == GraphAssociationType.Member ||
                     membership.atype == GraphAssociationType.Admin)))
            .OrderByDescending(friend => friend.time)
            .ThenByDescending(friend => friend.id2)
            .Select(friend => friend.id2)
            .Take(take)
            .ToArrayAsync(cancellationToken);
        if (friendIds.Length == 0)
        {
            return Array.Empty<GroupSuggestionFriendResult>();
        }

        var blockedIds = await _blockVisibility.GetBlockedUserIdsAsync(viewerId, friendIds, cancellationToken);
        var visibleFriendIds = friendIds.Where(friendId => !blockedIds.Contains(friendId)).ToArray();
        if (visibleFriendIds.Length == 0)
        {
            return Array.Empty<GroupSuggestionFriendResult>();
        }

        var profiles = await _userGraphService.GetProfilesForViewerAsync(
            viewerId,
            visibleFriendIds,
            cancellationToken);
        var profilesById = profiles.ToDictionary(profile => profile.Id);
        return visibleFriendIds
            .Where(profilesById.ContainsKey)
            .Select(friendId => profilesById[friendId])
            .Select(profile => new GroupSuggestionFriendResult(profile.Id, profile.Name, profile.Avatar))
            .ToArray();
    }

    public async Task<GroupResult?> ChangeGroupAvatarAsync(
        long actorId,
        long groupId,
        string avatarUrl,
        string? originalUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentGroup = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (currentGroup is null || currentGroup.otype != GraphObjectType.Group)
        {
            return null;
        }
        await EnsureMediaOwnedAsync(actorId, new[] { avatarUrl, originalUrl }, cancellationToken);
        var previousUrl = GraphJson.String(GraphJson.ParseObject(currentGroup.data), "avatar");
        var updated = await _objectService.UpdateObjectAsync(groupId, GraphObjectType.Group, GraphJson.PatchJson(("avatar", avatarUrl)), cancellationToken);
        if (updated is not null)
        {
            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                await _externalServiceClient.FinalizeMediaAsync(new[] { avatarUrl }, actorId, cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(previousUrl) &&
                !string.Equals(previousUrl, avatarUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Previous artwork may have been set by a different admin, so it is removed as
                // stored state rather than under the acting admin's ownership.
                await _externalServiceClient.DeleteMediaAsync(new[] { previousUrl }, null, cancellationToken);
            }
        }
        if (updated is not null && !string.IsNullOrWhiteSpace(originalUrl) && _contentGraphService is not null)
        {
            await _contentGraphService.CreateGroupPostAsync(
                new CreateGroupPostInput(
                    actorId,
                    groupId,
                    "đã cập nhật ảnh đại diện của nhóm.",
                    new[] { new MediaInput(GraphMediaType.Photo, originalUrl) }),
                cancellationToken);
        }

        return updated is null ? null : await GetGroupAsync(groupId, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupBackgroundAsync(
        long actorId,
        long groupId,
        string backgroundUrl,
        string? originalUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentGroup = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (currentGroup is null || currentGroup.otype != GraphObjectType.Group)
        {
            return null;
        }

        await EnsureMediaOwnedAsync(actorId, new[] { backgroundUrl, originalUrl }, cancellationToken);
        var previousUrl = GraphJson.String(GraphJson.ParseObject(currentGroup.data), "background");

        var updated = await _objectService.UpdateObjectAsync(
            groupId,
            GraphObjectType.Group,
            GraphJson.PatchJson(("background", backgroundUrl)),
            cancellationToken);

        if (updated is not null)
        {
            if (!string.IsNullOrWhiteSpace(backgroundUrl))
            {
                await _externalServiceClient.FinalizeMediaAsync(new[] { backgroundUrl }, actorId, cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(previousUrl) &&
                !string.Equals(previousUrl, backgroundUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Previous artwork may have been set by a different admin, so it is removed as
                // stored state rather than under the acting admin's ownership.
                await _externalServiceClient.DeleteMediaAsync(new[] { previousUrl }, null, cancellationToken);
            }
        }

        if (updated is not null && !string.IsNullOrWhiteSpace(originalUrl) && _contentGraphService is not null)
        {
            await _contentGraphService.CreateGroupPostAsync(
                new CreateGroupPostInput(
                    actorId,
                    groupId,
                    "đã cập nhật ảnh bìa của nhóm.",
                    new[] { new MediaInput(GraphMediaType.Photo, originalUrl) }),
                cancellationToken);
        }

        return updated is null ? null : await GetGroupAsync(groupId, cancellationToken);
    }

    public async Task<VisitedGroupPageResult> GetVisitedGroupsAsync(
        long userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 100);
        var query = _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId && item.atype == GraphAssociationType.Visited);
        if (TryDecodeVisitedGroupCursor(cursor, out var decodedCursor))
        {
            query = query.Where(item => item.time < decodedCursor.VisitTime ||
                item.time == decodedCursor.VisitTime && item.id2 < decodedCursor.GroupId);
        }

        var pageEdges = await query
            .OrderByDescending(item => item.time)
            .ThenByDescending(item => item.id2)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var selectedEdges = pageEdges.Take(take).ToArray();
        if (selectedEdges.Length == 0)
        {
            return new VisitedGroupPageResult(Array.Empty<VisitedGroupResult>(), null, false);
        }

        var groupIds = selectedEdges.Select(item => item.id2).Distinct().ToArray();
        var groups = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.id) && item.otype == GraphObjectType.Group)
            .ToDictionaryAsync(item => item.id, cancellationToken);
        var participatingGroupIds = (await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId &&
                groupIds.Contains(item.id2) &&
                (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin))
            .Select(item => item.id2)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();
        var items = new List<VisitedGroupResult>(selectedEdges.Length);

        foreach (var edge in selectedEdges)
        {
            if (!groups.TryGetValue(edge.id2, out var group))
            {
                continue;
            }

            var data = GraphJson.ParseObject(group.data);
            if (GraphJson.Int(data, "privacy") != 0 && !participatingGroupIds.Contains(group.id))
            {
                continue;
            }

            items.Add(new VisitedGroupResult(
                group.id,
                GraphJson.String(data, "avatar"),
                GraphJson.String(data, "name"),
                DateTimeOffset.FromUnixTimeMilliseconds(edge.time)
                    .UtcDateTime
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var lastScannedEdge = selectedEdges[^1];
        return new VisitedGroupPageResult(
            items,
            EncodeVisitedGroupCursor(lastScannedEdge.time, lastScannedEdge.id2),
            pageEdges.Count > take);
    }

    public async Task<bool> RecordGroupVisitAsync(
        long userId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (group is null || group.otype != GraphObjectType.Group)
        {
            return false;
        }

        if (!await CanViewGroupAsync(userId, group, cancellationToken))
        {
            return false;
        }

        return await _associationService.AddAssociationAsync(
            userId,
            GraphAssociationType.Visited,
            groupId,
            cancellationToken);
    }

    public Task<bool> IsAdminAsync(long userId, long groupId, CancellationToken cancellationToken = default)
    {
        return _associationService.HasAssociationAsync(
            userId,
            GraphAssociationType.Admin,
            groupId,
            cancellationToken);
    }

    public async Task<bool> RequestJoinAsync(long userId, long groupId, CancellationToken cancellationToken = default)
    {
        if (!await AreUserAndGroupAsync(userId, groupId, cancellationToken) ||
            await IsParticipantAsync(userId, groupId, cancellationToken) ||
            await _associationService.HasAssociationAsync(userId, GraphAssociationType.GroupJoinRequest, groupId, cancellationToken))
        {
            return false;
        }

        var requested = await _associationService.AddAssociationAsync(
            userId,
            GraphAssociationType.GroupJoinRequest,
            groupId,
            cancellationToken);
        if (!requested)
        {
            return false;
        }

        var admins = await _associationService.RetrieveAssociationAsync(
            groupId,
            GraphAssociationType.HaveAdmin,
            null,
            100,
            cancellationToken);
        foreach (var admin in admins.items)
        {
            await _externalServiceClient.NotifyAsync(
                userId,
                admin.id2,
                ExternalNotificationAction.GroupJoin,
                groupId,
                null,
                cancellationToken);
        }

        return true;
    }

    public Task<bool> CancelJoinRequestAsync(long userId, long groupId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            userId,
            GraphAssociationType.GroupJoinRequest,
            groupId,
            cancellationToken);
    }

    public async Task<bool> ApproveJoinRequestAsync(
        long adminId,
        long groupId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(adminId, groupId, cancellationToken) ||
            !await _associationService.HasAssociationAsync(userId, GraphAssociationType.GroupJoinRequest, groupId, cancellationToken))
        {
            return false;
        }

        var added = await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(userId, GraphAssociationType.GroupJoinRequest, groupId, false),
                new(userId, GraphAssociationType.Member, groupId, true)
            },
            cancellationToken);
        if (added)
        {
            await _externalServiceClient.NotifyAsync(
                adminId,
                userId,
                ExternalNotificationAction.GroupAccept,
                groupId,
                null,
                cancellationToken);
        }

        return added;
    }

    public async Task<bool> RejectJoinRequestAsync(
        long adminId,
        long groupId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsAdminAsync(adminId, groupId, cancellationToken))
        {
            return false;
        }

        return await _associationService.DeleteOneAssociationAsync(
            userId,
            GraphAssociationType.GroupJoinRequest,
            groupId,
            cancellationToken);
    }

    public async Task<bool> InviteUserAsync(
        long inviterId,
        long groupId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (!await IsParticipantAsync(inviterId, groupId, cancellationToken) ||
            !await AreUserAndGroupAsync(userId, groupId, cancellationToken) ||
            await IsParticipantAsync(userId, groupId, cancellationToken) ||
            await _associationService.HasAssociationAsync(
                userId,
                GraphAssociationType.GroupJoinRequest,
                groupId,
                cancellationToken) ||
            !await _associationService.HasAssociationAsync(
                inviterId,
                GraphAssociationType.Friend,
                userId,
                cancellationToken) ||
            await _associationService.HasAssociationAsync(
                inviterId,
                GraphAssociationType.Blocked,
                userId,
                cancellationToken) ||
            await _associationService.HasAssociationAsync(
                inviterId,
                GraphAssociationType.BlockedBy,
                userId,
                cancellationToken))
        {
            return false;
        }

        await _externalServiceClient.NotifyAsync(
            inviterId,
            userId,
            ExternalNotificationAction.GroupInvite,
            groupId,
            null,
            cancellationToken);
        return true;
    }

    public async Task<bool> LeaveGroupAsync(long userId, long groupId, CancellationToken cancellationToken = default)
    {
        return await _associationService.LeaveGroupWithAdminTransferAsync(
            userId,
            groupId,
            cancellationToken);
    }

    public Task<bool> AddMemberAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(userId, GraphAssociationType.GroupJoinRequest, groupId, false),
                new(userId, GraphAssociationType.Member, groupId, true)
            },
            cancellationToken);
    }

    public Task<bool> RemoveMemberAsync(
        long adminId,
        long groupId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        return _associationService.RemoveGroupMemberByAdminAsync(
            adminId,
            userId,
            groupId,
            cancellationToken);
    }

    public async Task<bool> AddAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(userId, GraphAssociationType.Member, groupId, true),
                new(userId, GraphAssociationType.Admin, groupId, true)
            },
            cancellationToken);
    }

    public async Task<bool> RemoveAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return await _associationService.DemoteGroupAdminAsync(userId, groupId, cancellationToken);
    }

    private async Task<bool> AreUserAndGroupAsync(long userId, long groupId, CancellationToken cancellationToken)
    {
        if (userId <= 0 || groupId <= 0)
        {
            return false;
        }

        var user = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        return user?.otype == GraphObjectType.User && group?.otype == GraphObjectType.Group;
    }

    public async Task<bool> IsParticipantAsync(long userId, long groupId, CancellationToken cancellationToken = default)
    {
        return await _associationService.HasAssociationAsync(userId, GraphAssociationType.Member, groupId, cancellationToken) ||
               await _associationService.HasAssociationAsync(userId, GraphAssociationType.Admin, groupId, cancellationToken);
    }

    private static void ValidateGroupPrivacy(int? privacy)
    {
        if (privacy is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(privacy),
                "Group privacy must be 0 (public) or 1 (private).");
        }
    }

    private async Task<bool> CanViewGroupAsync(
        long userId,
        SocialGraphObjectResult group,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(group.data);
        if (GraphJson.Int(data, "privacy") == 0)
        {
            return true;
        }

        return await _dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(
                item => item.id1 == userId &&
                    item.id2 == group.id &&
                    (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin),
                cancellationToken);
    }

    private static string EncodeVisitedGroupCursor(long visitTime, long groupId)
    {
        var payload = JsonSerializer.Serialize(new VisitedGroupCursor(visitTime, groupId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeVisitedGroupCursor(string? cursor, out VisitedGroupCursor decodedCursor)
    {
        decodedCursor = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parsed = JsonSerializer.Deserialize<VisitedGroupCursor>(json);
            if (parsed.VisitTime <= 0 || parsed.GroupId <= 0)
            {
                return false;
            }

            decodedCursor = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct VisitedGroupCursor(long VisitTime, long GroupId);
}
