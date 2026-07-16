namespace SocialGraph.Api.Service;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class SocialReadModelService : ISocialReadModelService
{
    private readonly MyDbContext _dbContext;
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IContentGraphService _contentGraphService;

    public SocialReadModelService(
        MyDbContext dbContext,
        IObjectService objectService,
        IAssociationService associationService,
        IContentGraphService contentGraphService)
    {
        _dbContext = dbContext;
        _objectService = objectService;
        _associationService = associationService;
        _contentGraphService = contentGraphService;
    }

    public async Task<UserRelationshipStateResult?> GetUserRelationshipStateAsync(
        long viewerId,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (user?.otype != GraphObjectType.User)
        {
            return null;
        }

        if (viewerId == userId)
        {
            return new UserRelationshipStateResult(userId, true, false, false, false, false, false, false, false);
        }

        var isBlocked = await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken);
        var isBlockedBy = await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken);
        if (isBlocked || isBlockedBy)
        {
            return new UserRelationshipStateResult(userId, false, false, false, false, false, false, isBlocked, isBlockedBy);
        }

        var isFriend = await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Friend, userId, cancellationToken);
        var isFollowing = !isFriend && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Followed, userId, cancellationToken);
        var followsViewer = !isFriend && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.FollowedBy, userId, cancellationToken);
        return new UserRelationshipStateResult(
            userId,
            false,
            isFriend,
            isFollowing,
            followsViewer,
            !isFriend && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.FriendRequest, userId, cancellationToken),
            !isFriend && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.HaveFriendRequest, userId, cancellationToken),
            false,
            false);
    }

    public async Task<GroupViewerStateResult?> GetGroupViewerStateAsync(
        long viewerId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (group?.otype != GraphObjectType.Group)
        {
            return null;
        }

        var isAdmin = await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Admin, groupId, cancellationToken);
        var isMember = !isAdmin && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Member, groupId, cancellationToken);
        var pending = !isAdmin && !isMember && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.GroupJoinRequest, groupId, cancellationToken);
        var isPublic = GraphJson.Int(GraphJson.ParseObject(group.data), "privacy") == 0;
        return new GroupViewerStateResult(groupId, isMember, isAdmin, pending, isPublic || isMember || isAdmin);
    }

    public async Task<GroupMembershipPageResult> GetPendingGroupJoinsAsync(
        long userId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var page = await _associationService.RetrieveAssociationAsync(
            userId,
            GraphAssociationType.GroupJoinRequest,
            cursor,
            Math.Clamp(limit, 1, 50),
            cancellationToken);
        var groups = new List<GroupResult>(page.items.Count);
        foreach (var edge in page.items)
        {
            var group = await BuildGroupAsync(edge.id2, cancellationToken);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        return new GroupMembershipPageResult(groups, page.nextCursor, page.nextCursor is not null);
    }

    public async Task<UserSummaryPageResult> GetGroupMembersAsync(
        long viewerId,
        long groupId,
        string? cursor,
        int limit,
        bool admins,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewGroupAsync(viewerId, groupId, cancellationToken))
        {
            return EmptyUsers();
        }

        return await GetAssociatedUsersAsync(
            groupId,
            admins ? GraphAssociationType.HaveAdmin : GraphAssociationType.HaveMember,
            cursor,
            limit,
            cancellationToken);
    }

    public async Task<GroupPostPageResult> GetGroupPostsAsync(
        long viewerId,
        long groupId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewGroupAsync(viewerId, groupId, cancellationToken))
        {
            return new GroupPostPageResult(Array.Empty<GroupPostDetailResult>(), null, false);
        }

        var take = Math.Clamp(limit, 1, 25);
        var page = await _associationService.RetrieveAssociationAsync(
            groupId,
            GraphAssociationType.Published,
            cursor,
            Math.Min(take * 2, 50),
            cancellationToken);
        var posts = await _contentGraphService.GetPostDetailsAsync(
            viewerId,
            page.items.Select(item => item.id2).ToArray(),
            cancellationToken);
        var byId = posts.OfType<GroupPostDetailResult>().ToDictionary(item => item.Id);
        var items = new List<GroupPostDetailResult>(take);
        var processed = 0;
        foreach (var edge in page.items)
        {
            processed++;
            if (byId.TryGetValue(edge.id2, out var post))
            {
                items.Add(post);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNext = processed < page.items.Count || page.nextCursor is not null;
        return new GroupPostPageResult(items, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    public async Task<GroupPostPageResult> GetGroupUserPostsAsync(
        long viewerId,
        long groupId,
        long userId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewGroupAsync(viewerId, groupId, cancellationToken))
        {
            return new GroupPostPageResult(Array.Empty<GroupPostDetailResult>(), null, false);
        }

        var take = Math.Clamp(limit, 1, 25);
        var page = await _associationService.RetrieveAssociationAsync(
            groupId,
            GraphAssociationType.Published,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var posts = await _contentGraphService.GetPostDetailsAsync(
            viewerId,
            page.items.Select(item => item.id2).ToArray(),
            cancellationToken);
        var byId = posts
            .OfType<GroupPostDetailResult>()
            .Where(item => item.Author.Id == userId)
            .ToDictionary(item => item.Id);
        var items = new List<GroupPostDetailResult>(take);
        var processed = 0;
        foreach (var edge in page.items)
        {
            processed++;
            if (byId.TryGetValue(edge.id2, out var post))
            {
                items.Add(post);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNext = processed < page.items.Count || page.nextCursor is not null;
        return new GroupPostPageResult(items, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    public async Task<MediaPageResult> GetOwnedMediaAsync(
        long viewerId,
        long ownerId,
        int? type,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var owner = await _objectService.RetrieveObjectAsync(ownerId, cancellationToken);
        if (owner?.otype is not (GraphObjectType.User or GraphObjectType.Group))
        {
            return EmptyMedia();
        }

        var canManage = owner.otype == GraphObjectType.User
            ? viewerId == ownerId
            : await _associationService.HasAssociationAsync(
                viewerId,
                GraphAssociationType.Admin,
                ownerId,
                cancellationToken);
        if (!canManage && owner.otype == GraphObjectType.User && await IsBlockedAsync(viewerId, ownerId, cancellationToken))
        {
            return EmptyMedia();
        }

        if (!canManage && owner.otype == GraphObjectType.Group && !await CanViewGroupAsync(viewerId, ownerId, cancellationToken))
        {
            return EmptyMedia();
        }

        var take = Math.Clamp(limit, 1, 25);
        var page = await _associationService.RetrieveAssociationAsync(
            ownerId,
            GraphAssociationType.Owned,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var items = new List<MediaResult>(take);
        var processed = 0;
        foreach (var edge in page.items)
        {
            processed++;
            var media = await _objectService.RetrieveObjectAsync(edge.id2, cancellationToken);
            if (media?.otype != GraphObjectType.Media)
            {
                continue;
            }

            var data = GraphJson.ParseObject(media.data);
            var mediaType = GraphJson.Int(data, "type");
            if (type is not null && type.Value != mediaType)
            {
                continue;
            }

            if (!canManage && !await IsOwnedMediaVisibleAsync(viewerId, media.id, cancellationToken))
            {
                continue;
            }

            items.Add(new MediaResult(media.id, mediaType, GraphJson.String(data, "url")));
            if (items.Count == take)
            {
                break;
            }
        }

        var hasNext = processed < page.items.Count || page.nextCursor is not null;
        return new MediaPageResult(items, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    public Task<ProfileReelPageResult> GetLikedReelsAsync(
        long viewerId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return GetAssociatedReelsAsync(viewerId, GraphAssociationType.Liked, cursor, limit, cancellationToken);
    }

    public Task<ProfileReelPageResult> GetWatchedReelsAsync(
        long viewerId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return GetAssociatedReelsAsync(viewerId, GraphAssociationType.Watched, cursor, limit, cancellationToken);
    }

    public async Task<ProfileReelPageResult> GetSharedReelsAsync(
        long viewerId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var authoredIds = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == viewerId && item.atype == GraphAssociationType.Authored)
            .Select(item => item.id2)
            .ToArrayAsync(cancellationToken);
        if (authoredIds.Length == 0)
        {
            return EmptyReels();
        }

        var take = Math.Clamp(limit, 1, 25);
        var scan = Math.Min(take * 4, 100);
        var offset = ParseOffset(cursor);
        var candidates = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.atype == GraphAssociationType.Share && authoredIds.Contains(item.id1))
            .GroupBy(item => item.id2)
            .Select(group => new { Id = group.Key, Time = group.Max(item => item.time) })
            .OrderByDescending(item => item.Time)
            .ThenByDescending(item => item.Id)
            .Skip((int)Math.Min(offset, int.MaxValue))
            .Take(scan + 1)
            .ToListAsync(cancellationToken);
        var items = new List<ContentResult>(take);
        var processed = 0;
        foreach (var candidate in candidates.Take(scan))
        {
            processed++;
            var reel = await GetVisibleReelAsync(viewerId, candidate.Id, cancellationToken);
            if (reel is not null)
            {
                items.Add(reel);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNext = processed < candidates.Count || candidates.Count > scan;
        return new ProfileReelPageResult(items, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    public async Task<CommentPageResult> GetCommentsAsync(
        long viewerId,
        long targetId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken))
        {
            return new CommentPageResult(Array.Empty<CommentThreadItemResult>(), null, false);
        }

        var page = await _associationService.RetrieveAssociationAsync(
            targetId,
            GraphAssociationType.HaveComment,
            cursor,
            Math.Clamp(limit, 1, 50),
            cancellationToken);
        if (page.items.Count == 0)
        {
            return new CommentPageResult(Array.Empty<CommentThreadItemResult>(), null, false);
        }

        var commentIds = page.items.Select(item => item.id2).Distinct().ToArray();
        var comments = await _dbContext.ObjectsTb.AsNoTracking()
            .Where(item => commentIds.Contains(item.id) && item.otype == GraphObjectType.Comment)
            .ToDictionaryAsync(item => item.id, cancellationToken);
        var links = await _dbContext.AssociationsTb.AsNoTracking()
            .Where(item => commentIds.Contains(item.id1) &&
                (item.atype == GraphAssociationType.AuthoredBy ||
                 item.atype == GraphAssociationType.LikedBy ||
                 item.atype == GraphAssociationType.HaveComment))
            .ToListAsync(cancellationToken);
        var viewerLikes = (await _dbContext.AssociationsTb.AsNoTracking()
            .Where(item => item.id1 == viewerId && item.atype == GraphAssociationType.Liked && commentIds.Contains(item.id2))
            .Select(item => item.id2)
            .ToListAsync(cancellationToken))
            .ToHashSet();
        var authorByComment = links.Where(item => item.atype == GraphAssociationType.AuthoredBy)
            .GroupBy(item => item.id1)
            .ToDictionary(group => group.Key, group => group.First().id2);
        var summaries = await GetUserSummariesAsync(authorByComment.Values.Distinct().ToArray(), cancellationToken);
        var result = new List<CommentThreadItemResult>(page.items.Count);
        foreach (var edge in page.items)
        {
            if (!comments.TryGetValue(edge.id2, out var comment) ||
                !authorByComment.TryGetValue(edge.id2, out var authorId) ||
                !summaries.TryGetValue(authorId, out var author))
            {
                continue;
            }

            var data = GraphJson.ParseObject(comment.data);
            result.Add(new CommentThreadItemResult(
                comment.id,
                GraphJson.String(data, "content"),
                GraphJson.String(data, "create"),
                author,
                links.LongCount(item => item.id1 == comment.id && item.atype == GraphAssociationType.LikedBy),
                links.LongCount(item => item.id1 == comment.id && item.atype == GraphAssociationType.HaveComment),
                viewerLikes.Contains(comment.id)));
        }

        return new CommentPageResult(result, page.nextCursor, page.nextCursor is not null);
    }

    public async Task<ContentEngagementResult?> GetEngagementAsync(
        long viewerId,
        long targetId,
        CancellationToken cancellationToken = default)
    {
        if (!await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken))
        {
            return null;
        }

        return new ContentEngagementResult(
            targetId,
            await _associationService.CountAssociationAsync(targetId, GraphAssociationType.LikedBy, cancellationToken),
            await _associationService.CountAssociationAsync(targetId, GraphAssociationType.HaveComment, cancellationToken),
            await _associationService.CountAssociationAsync(targetId, GraphAssociationType.SharedBy, cancellationToken),
            await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Liked, targetId, cancellationToken),
            await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Saved, targetId, cancellationToken),
            await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Watched, targetId, cancellationToken));
    }

    public async Task<SavedContentPageResult> GetSavedContentAsync(
        long viewerId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit, 1, 25);
        var page = await _associationService.RetrieveAssociationAsync(
            viewerId,
            GraphAssociationType.Saved,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var result = new List<SavedContentItemResult>(take);
        var processed = 0;
        foreach (var edge in page.items)
        {
            processed++;
            var item = await _objectService.RetrieveObjectAsync(edge.id2, cancellationToken);
            if (item?.otype is GraphObjectType.FeedPost or GraphObjectType.GroupPost)
            {
                var post = await _contentGraphService.GetPostDetailAsync(viewerId, item.id, cancellationToken);
                if (post is not null)
                {
                    result.Add(new SavedContentItemResult(item.id, item.otype, post, null));
                }
            }
            else if (item?.otype == GraphObjectType.Reel && await CanViewTargetCoreAsync(viewerId, item.id, 0, cancellationToken))
            {
                var reel = await _contentGraphService.GetContentAsync(item.id, cancellationToken);
                if (reel is not null)
                {
                    result.Add(new SavedContentItemResult(item.id, item.otype, null, reel));
                }
            }

            if (result.Count == take)
            {
                break;
            }
        }

        var hasNext = processed < page.items.Count || page.nextCursor is not null;
        return new SavedContentPageResult(result, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    public async Task<UserSummaryPageResult> GetLikedUsersAsync(long viewerId, long targetId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken)
            ? await GetAssociatedUsersAsync(targetId, GraphAssociationType.LikedBy, cursor, limit, cancellationToken)
            : EmptyUsers();
    }

    public async Task<UserSummaryPageResult> GetStoryViewersAsync(long viewerId, long storyId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var story = await _objectService.RetrieveObjectAsync(storyId, cancellationToken);
        if (story?.otype != GraphObjectType.Story ||
            !await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Authored, storyId, cancellationToken))
        {
            return EmptyUsers();
        }

        return await GetAssociatedUsersAsync(storyId, GraphAssociationType.WatchedBy, cursor, limit, cancellationToken);
    }

    public async Task<UserSummaryPageResult> GetTaggedUsersAsync(long viewerId, long postId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return await CanViewTargetCoreAsync(viewerId, postId, 0, cancellationToken)
            ? await GetAssociatedUsersAsync(postId, GraphAssociationType.Tagged, cursor, limit, cancellationToken)
            : EmptyUsers();
    }

    public async Task<UserSummaryPageResult> GetMentionedUsersAsync(long viewerId, long sourceId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return await CanViewTargetCoreAsync(viewerId, sourceId, 0, cancellationToken)
            ? await GetAssociatedUsersAsync(sourceId, GraphAssociationType.Mentioned, cursor, limit, cancellationToken)
            : EmptyUsers();
    }

    public Task<bool> CanViewTargetAsync(
        long viewerId,
        long targetId,
        CancellationToken cancellationToken = default)
    {
        return CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken);
    }

    public Task<bool> CanCommentTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default)
    {
        return CanUseTargetAsync(
            viewerId,
            targetId,
            new[] { GraphObjectType.FeedPost, GraphObjectType.GroupPost, GraphObjectType.Reel, GraphObjectType.Comment },
            cancellationToken);
    }

    public Task<bool> CanSaveTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default)
    {
        return CanUseTargetAsync(
            viewerId,
            targetId,
            new[] { GraphObjectType.FeedPost, GraphObjectType.GroupPost, GraphObjectType.Reel },
            cancellationToken);
    }

    public Task<bool> CanWatchTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default)
    {
        return CanUseTargetAsync(
            viewerId,
            targetId,
            new[] { GraphObjectType.Reel, GraphObjectType.Story },
            cancellationToken);
    }

    public async Task<bool> CanShareTargetAsync(
        long viewerId,
        long targetId,
        CancellationToken cancellationToken = default)
    {
        var target = await _objectService.RetrieveObjectAsync(targetId, cancellationToken);
        if (target?.otype == GraphObjectType.Reel)
        {
            return await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken);
        }

        return target?.otype == GraphObjectType.FeedPost &&
               GraphJson.Int(GraphJson.ParseObject(target.data), "privacy") == 0 &&
               await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken);
    }

    private async Task<bool> CanViewGroupAsync(long viewerId, long groupId, CancellationToken cancellationToken)
    {
        var state = await GetGroupViewerStateAsync(viewerId, groupId, cancellationToken);
        return state?.CanViewPosts == true;
    }

    private async Task<bool> CanUseTargetAsync(
        long viewerId,
        long targetId,
        IReadOnlyCollection<short> allowedTypes,
        CancellationToken cancellationToken)
    {
        var target = await _objectService.RetrieveObjectAsync(targetId, cancellationToken);
        return target is not null &&
               allowedTypes.Contains(target.otype) &&
               await CanViewTargetCoreAsync(viewerId, targetId, 0, cancellationToken);
    }

    private async Task<bool> CanViewTargetCoreAsync(long viewerId, long targetId, int depth, CancellationToken cancellationToken)
    {
        if (depth > 20)
        {
            return false;
        }

        var target = await _objectService.RetrieveObjectAsync(targetId, cancellationToken);
        if (target is null)
        {
            return false;
        }

        if (target.otype is GraphObjectType.FeedPost or GraphObjectType.GroupPost)
        {
            return await _contentGraphService.GetPostDetailAsync(viewerId, targetId, cancellationToken) is not null;
        }

        if (target.otype == GraphObjectType.Comment)
        {
            var parent = await _associationService.RetrieveAssociationAsync(
                targetId,
                GraphAssociationType.Comment,
                null,
                1,
                cancellationToken);
            return parent.items.Count > 0 &&
                   await CanViewTargetCoreAsync(viewerId, parent.items[0].id2, depth + 1, cancellationToken);
        }

        if (target.otype is not (GraphObjectType.Reel or GraphObjectType.Story))
        {
            return false;
        }

        if (target.otype == GraphObjectType.Story)
        {
            var expires = GraphJson.String(GraphJson.ParseObject(target.data), "expire");
            if (DateTimeOffset.TryParse(expires, out var expiresAt) && expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }
        }

        var author = await _associationService.RetrieveAssociationAsync(
            targetId,
            GraphAssociationType.AuthoredBy,
            null,
            1,
            cancellationToken);
        var authorId = author.items.FirstOrDefault()?.id2 ?? 0;
        if (authorId <= 0 || await IsBlockedAsync(viewerId, authorId, cancellationToken))
        {
            return false;
        }

        if (target.otype == GraphObjectType.Reel || viewerId == authorId)
        {
            return true;
        }

        var authorObject = await _objectService.RetrieveObjectAsync(authorId, cancellationToken);
        var privacy = authorObject is null ? 0 : GraphJson.Int(GraphJson.ParseObject(authorObject.data), "privacy");
        var isFriend = await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Friend, authorId, cancellationToken);
        return isFriend || privacy == 1 && await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Followed, authorId, cancellationToken);
    }

    private async Task<bool> IsBlockedAsync(long viewerId, long userId, CancellationToken cancellationToken)
    {
        return viewerId != userId &&
               (await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken) ||
                await _associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken));
    }

    private async Task<bool> IsOwnedMediaVisibleAsync(
        long viewerId,
        long mediaId,
        CancellationToken cancellationToken)
    {
        var containerIds = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.atype == GraphAssociationType.Contained && item.id2 == mediaId)
            .Select(item => item.id1)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        foreach (var containerId in containerIds)
        {
            if (await CanViewTargetCoreAsync(viewerId, containerId, 0, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<ProfileReelPageResult> GetAssociatedReelsAsync(
        long viewerId,
        short associationType,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, 25);
        var page = await _associationService.RetrieveAssociationAsync(
            viewerId,
            associationType,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var items = new List<ContentResult>(take);
        var processed = 0;
        foreach (var edge in page.items)
        {
            processed++;
            var reel = await GetVisibleReelAsync(viewerId, edge.id2, cancellationToken);
            if (reel is not null)
            {
                items.Add(reel);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNext = processed < page.items.Count || page.nextCursor is not null;
        return new ProfileReelPageResult(items, hasNext ? AdvanceCursor(cursor, processed) : null, hasNext);
    }

    private async Task<ContentResult?> GetVisibleReelAsync(
        long viewerId,
        long reelId,
        CancellationToken cancellationToken)
    {
        var item = await _objectService.RetrieveObjectAsync(reelId, cancellationToken);
        if (item?.otype != GraphObjectType.Reel ||
            !await CanViewTargetCoreAsync(viewerId, reelId, 0, cancellationToken))
        {
            return null;
        }

        return await _contentGraphService.GetContentAsync(reelId, cancellationToken);
    }

    private async Task<UserSummaryPageResult> GetAssociatedUsersAsync(
        long sourceId,
        short associationType,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = await _associationService.RetrieveAssociationAsync(
            sourceId,
            associationType,
            cursor,
            Math.Clamp(limit, 1, 50),
            cancellationToken);
        var summaries = await GetUserSummariesAsync(page.items.Select(item => item.id2).ToArray(), cancellationToken);
        var items = page.items
            .Where(item => summaries.ContainsKey(item.id2))
            .Select(item => summaries[item.id2])
            .ToArray();
        return new UserSummaryPageResult(items, page.nextCursor, page.nextCursor is not null);
    }

    private async Task<IReadOnlyDictionary<long, UserSummaryResult>> GetUserSummariesAsync(
        IReadOnlyCollection<long> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<long, UserSummaryResult>();
        }

        var users = await _dbContext.ObjectsTb.AsNoTracking()
            .Where(item => userIds.Contains(item.id) && item.otype == GraphObjectType.User)
            .ToListAsync(cancellationToken);
        return users.ToDictionary(item => item.id, item =>
        {
            var data = GraphJson.ParseObject(item.data);
            var verify = GraphJson.String(data, "verify");
            return new UserSummaryResult(
                item.id,
                GraphJson.String(data, "name"),
                GraphJson.String(data, "avatar"),
                DateTimeOffset.TryParse(verify, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow);
        });
    }

    private async Task<GroupResult?> BuildGroupAsync(long groupId, CancellationToken cancellationToken)
    {
        var item = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (item?.otype != GraphObjectType.Group)
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
            await _associationService.CountAssociationAsync(item.id, GraphAssociationType.HaveMember, cancellationToken),
            await _associationService.CountAssociationAsync(item.id, GraphAssociationType.HaveAdmin, cancellationToken));
    }

    private static UserSummaryPageResult EmptyUsers() => new(Array.Empty<UserSummaryResult>(), null, false);

    private static MediaPageResult EmptyMedia() => new(Array.Empty<MediaResult>(), null, false);

    private static ProfileReelPageResult EmptyReels() => new(Array.Empty<ContentResult>(), null, false);

    private static long ParseOffset(string? cursor)
    {
        return long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    private static string AdvanceCursor(string? cursor, int processed)
    {
        var start = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;
        return (start + processed).ToString(CultureInfo.InvariantCulture);
    }
}
