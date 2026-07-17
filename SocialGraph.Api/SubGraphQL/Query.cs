namespace SocialGraph.Api.SubGraphQL;

using System.Globalization;
using HotChocolate;
using HotChocolate.Types;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;

public class Query
{
    private const int MaxBatchLookupIds = 50;

    [GraphQLIgnore]
    public Task<FederatedUser?> GetUserByIdAsync(
        long id,
        [Service] IUserGraphService userGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return FederatedUser.ResolveForViewerAsync(
            trustedCaller.RequireUserId(),
            id,
            userGraphService,
            associationService,
            cancellationToken);
    }

    public async Task<IReadOnlyList<UserProfileResult>> GetProfilesAsync(
        IReadOnlyList<long> userIds,
        [Service] IUserGraphService userGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        EnsureBatchSize(userIds.Count);
        var viewerId = trustedCaller.RequireUserId();
        return await userGraphService.GetProfilesForViewerAsync(viewerId, userIds, cancellationToken);
    }

    public async Task<IReadOnlyList<GroupResult>> GetGroupsAsync(
        IReadOnlyList<long> groupIds,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        EnsureBatchSize(groupIds.Count);
        trustedCaller.RequireUserId();
        var groups = new List<GroupResult>(groupIds.Count);
        foreach (var groupId in groupIds.Distinct())
        {
            var group = await groupGraphService.GetGroupAsync(groupId, cancellationToken);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    public async Task<ProfilePostPageResult> GetProfilePostsAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IContentGraphService contentGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        if (await IsBlockedAsync(viewerId, userId, associationService, cancellationToken))
        {
            return new ProfilePostPageResult(Array.Empty<IHomePostResult>(), null, false);
        }

        var take = Math.Clamp(limit, 1, 25);
        var authored = await associationService.RetrieveAssociationAsync(
            userId,
            GraphAssociationType.Authored,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var details = await contentGraphService.GetPostDetailsAsync(
            viewerId,
            authored.items.Select(item => item.id2).ToArray(),
            cancellationToken);
        var detailsById = details.ToDictionary(PostId);
        var items = new List<IHomePostResult>(take);
        var processed = 0;
        foreach (var edge in authored.items)
        {
            processed++;
            if (detailsById.TryGetValue(edge.id2, out var detail))
            {
                items.Add(detail);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNextPage = processed < authored.items.Count || authored.nextCursor is not null;
        return new ProfilePostPageResult(
            items,
            hasNextPage ? AdvanceCursor(cursor, processed) : null,
            hasNextPage);
    }

    public async Task<ProfileReelPageResult> GetProfileReelsAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IContentGraphService contentGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        if (await IsBlockedAsync(viewerId, userId, associationService, cancellationToken))
        {
            return new ProfileReelPageResult(Array.Empty<ContentResult>(), null, false);
        }

        var take = Math.Clamp(limit, 1, 25);
        var authored = await associationService.RetrieveAssociationAsync(
            userId,
            GraphAssociationType.Authored,
            cursor,
            Math.Min(take * 4, 100),
            cancellationToken);
        var items = new List<ContentResult>(take);
        var processed = 0;
        foreach (var edge in authored.items)
        {
            processed++;
            var content = await contentGraphService.GetContentAsync(edge.id2, cancellationToken);
            if (content?.Type == GraphObjectType.Reel)
            {
                items.Add(content);
                if (items.Count == take)
                {
                    break;
                }
            }
        }

        var hasNextPage = processed < authored.items.Count || authored.nextCursor is not null;
        return new ProfileReelPageResult(
            items,
            hasNextPage ? AdvanceCursor(cursor, processed) : null,
            hasNextPage);
    }

    public async Task<UserSearchHydrationResult?> GetUserSearchResultAsync(
        [GraphQLType(typeof(NonNullType<IdType>))] long referenceId,
        [Service] IUserGraphService userGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var user = await FederatedUser.ResolveForViewerAsync(
            trustedCaller.RequireUserId(),
            referenceId,
            userGraphService,
            associationService,
            cancellationToken);
        return user is null ? null : new UserSearchHydrationResult(referenceId, user);
    }

    public async Task<GroupSearchHydrationResult?> GetGroupSearchResultAsync(
        [GraphQLType(typeof(NonNullType<IdType>))] long referenceId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId();
        var group = await groupGraphService.GetGroupAsync(referenceId, cancellationToken);
        return group is null ? null : new GroupSearchHydrationResult(referenceId, group);
    }

    public async Task<FeedPostSearchHydrationResult?> GetFeedPostSearchResultAsync(
        [GraphQLType(typeof(NonNullType<IdType>))] long referenceId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var post = await contentGraphService.GetPostDetailAsync(
            trustedCaller.RequireUserId(),
            referenceId,
            cancellationToken);
        return post is FeedPostDetailResult feedPost
            ? new FeedPostSearchHydrationResult(referenceId, feedPost)
            : null;
    }

    public async Task<GroupPostSearchHydrationResult?> GetGroupPostSearchResultAsync(
        [GraphQLType(typeof(NonNullType<IdType>))] long referenceId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var post = await contentGraphService.GetPostDetailAsync(
            trustedCaller.RequireUserId(),
            referenceId,
            cancellationToken);
        return post is GroupPostDetailResult groupPost
            ? new GroupPostSearchHydrationResult(referenceId, groupPost)
            : null;
    }

    public async Task<ReelSearchHydrationResult?> GetReelSearchResultAsync(
        [GraphQLType(typeof(NonNullType<IdType>))] long referenceId,
        [Service] IContentGraphService contentGraphService,
        [Service] IUserGraphService userGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        var reel = await contentGraphService.GetContentAsync(referenceId, cancellationToken);
        if (reel is null || reel.Type != GraphObjectType.Reel)
        {
            return null;
        }

        if (viewerId != reel.AuthorId &&
            (await associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, reel.AuthorId, cancellationToken) ||
             await associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, reel.AuthorId, cancellationToken)))
        {
            return null;
        }

        var author = await userGraphService.GetProfileAsync(reel.AuthorId, cancellationToken);
        return author is null
            ? null
            : new ReelSearchHydrationResult(
                referenceId,
                reel,
                new UserSummaryResult(author.Id, author.Name, author.Avatar, author.IsVerified));
    }

    public RecommendationItemResult? GetRecommendationItem(
        [GraphQLType(typeof(NonNullType<IdType>))] long postId)
    {
        return postId > 0 ? new RecommendationItemResult(postId) : null;
    }

    public ReelRecommendationItemResult? GetReelRecommendationItem(
        [GraphQLType(typeof(NonNullType<IdType>))] long reelId)
    {
        return reelId > 0 ? new ReelRecommendationItemResult(reelId) : null;
    }

    public async Task<UserProfileResult?> GetProfileAsync(
        long userId,
        [Service] IUserGraphService userGraphService,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        if (viewerId != userId &&
            (await associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken) ||
             await associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken)))
        {
            return null;
        }

        return await userGraphService.GetProfileAsync(userId, cancellationToken);
    }

    public Task<GroupResult?> GetGroupAsync(
        long groupId,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId();
        return groupGraphService.GetGroupAsync(groupId, cancellationToken);
    }

    public Task<VisitedGroupPageResult> GetVisitedGroupsAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return groupGraphService.GetVisitedGroupsAsync(userId, limit, cursor, cancellationToken);
    }

    public Task<IHomePostResult?> GetPostDetailAsync(
        long postId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        return contentGraphService.GetPostDetailAsync(viewerId, postId, cancellationToken);
    }

    public Task<IReadOnlyList<IHomePostResult>> GetPostDetailsAsync(
        IReadOnlyList<long> postIds,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        if (postIds.Count > ContentGraphService.MaxPostDetailIds)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetCode("BAD_USER_INPUT")
                    .SetMessage($"At most {ContentGraphService.MaxPostDetailIds} post IDs can be requested.")
                    .Build());
        }

        var viewerId = trustedCaller.RequireUserId();
        return contentGraphService.GetPostDetailsAsync(viewerId, postIds, cancellationToken);
    }

    public Task<HomeStoryPageResult> GetHomeStoriesAsync(
        long userId,
        int limit,
        string? cursor,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.GetHomeStoriesAsync(userId, limit, cursor, cancellationToken);
    }

    public Task<HomeStoryBucketResult?> GetMyStoriesAsync(
        long userId,
        [Service] IContentGraphService contentGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return contentGraphService.GetMyStoriesAsync(userId, cancellationToken);
    }

    public Task<AssociationPageResult> GetFriendsAsync(
        long userId,
        string? cursor,
        int limit,
        [Service] IAssociationService associationService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.Friend, cursor, limit, cancellationToken);
    }

    public Task<AssociationPageResult> GetIncomingFriendRequestsAsync(long userId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.HaveFriendRequest, cursor, limit, cancellationToken);
    }

    public Task<AssociationPageResult> GetOutgoingFriendRequestsAsync(long userId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.FriendRequest, cursor, limit, cancellationToken);
    }

    public Task<AssociationPageResult> GetFollowingAsync(long userId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.Followed, cursor, limit, cancellationToken);
    }

    public Task<AssociationPageResult> GetFollowersAsync(long userId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.FollowedBy, cursor, limit, cancellationToken);
    }

    public Task<AssociationPageResult> GetBlockedUsersAsync(long userId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return associationService.RetrieveAssociationAsync(userId, GraphAssociationType.Blocked, cursor, limit, cancellationToken);
    }

    public Task<GroupMembershipPageResult> GetMemberGroupsAsync(
        long userId,
        string? cursor,
        int limit,
        [Service] IAssociationService associationService,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return GetGroupMembershipPageAsync(
            userId,
            GraphAssociationType.Member,
            cursor,
            limit,
            associationService,
            groupGraphService,
            cancellationToken);
    }

    public Task<GroupMembershipPageResult> GetAdminGroupsAsync(
        long userId,
        string? cursor,
        int limit,
        [Service] IAssociationService associationService,
        [Service] IGroupGraphService groupGraphService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return GetGroupMembershipPageAsync(
            userId,
            GraphAssociationType.Admin,
            cursor,
            limit,
            associationService,
            groupGraphService,
            cancellationToken);
    }

    public Task<UserRelationshipStateResult?> GetRelationshipStateAsync(
        long userId,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetUserRelationshipStateAsync(
            trustedCaller.RequireUserId(),
            userId,
            cancellationToken);
    }

    public Task<GroupViewerStateResult?> GetGroupViewerStateAsync(
        long groupId,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetGroupViewerStateAsync(
            trustedCaller.RequireUserId(),
            groupId,
            cancellationToken);
    }

    public Task<GroupMembershipPageResult> GetPendingGroupJoinsAsync(
        long userId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return readModels.GetPendingGroupJoinsAsync(userId, cursor, limit, cancellationToken);
    }

    public Task<UserSummaryPageResult> GetGroupMembersAsync(
        long groupId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetGroupMembersAsync(
            trustedCaller.RequireUserId(),
            groupId,
            cursor,
            limit,
            false,
            cancellationToken);
    }

    public Task<UserSummaryPageResult> GetGroupAdminsAsync(
        long groupId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetGroupMembersAsync(
            trustedCaller.RequireUserId(),
            groupId,
            cursor,
            limit,
            true,
            cancellationToken);
    }

    public Task<GroupPostPageResult> GetGroupPostsAsync(
        long groupId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetGroupPostsAsync(
            trustedCaller.RequireUserId(),
            groupId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<GroupPostPageResult> GetGroupUserPostsAsync(
        long groupId,
        long userId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetGroupUserPostsAsync(
            trustedCaller.RequireUserId(),
            groupId,
            userId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<PhotoPageResult> GetUserPhotosAsync(
        long userId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetUserPhotosAsync(
            trustedCaller.RequireUserId(),
            userId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<PhotoPageResult> GetGroupPhotosAsync(
        long groupId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken) =>
        readModels.GetGroupPhotosAsync(
            trustedCaller.RequireUserId(), groupId, cursor, limit, cancellationToken);

    public Task<PhotoPageResult> GetGroupUserPhotosAsync(
        long groupId,
        long userId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken) =>
        readModels.GetGroupUserPhotosAsync(
            trustedCaller.RequireUserId(), groupId, userId, cursor, limit, cancellationToken);

    public Task<PhotoPageResult> GetMyFeedPhotoCandidatesAsync(
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken) =>
        readModels.GetMyFeedPhotoCandidatesAsync(
            trustedCaller.RequireUserId(), cursor, limit, cancellationToken);

    public Task<PhotoPageResult> GetGroupPhotoCandidatesAsync(
        long groupId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken) =>
        readModels.GetGroupPhotoCandidatesAsync(
            trustedCaller.RequireUserId(), groupId, cursor, limit, cancellationToken);

    public Task<ProfileReelPageResult> GetLikedReelsAsync(
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetLikedReelsAsync(
            trustedCaller.RequireUserId(),
            cursor,
            limit,
            cancellationToken);
    }

    public Task<ProfileReelPageResult> GetSharedReelsAsync(
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetSharedReelsAsync(
            trustedCaller.RequireUserId(),
            cursor,
            limit,
            cancellationToken);
    }

    public Task<ProfileReelPageResult> GetWatchedReelsAsync(
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetWatchedReelsAsync(
            trustedCaller.RequireUserId(),
            cursor,
            limit,
            cancellationToken);
    }

    public Task<CommentPageResult> GetCommentsAsync(
        long targetId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetCommentsAsync(
            trustedCaller.RequireUserId(),
            targetId,
            cursor,
            limit,
            cancellationToken);
    }

    public Task<ContentEngagementResult?> GetContentEngagementAsync(
        long targetId,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetEngagementAsync(trustedCaller.RequireUserId(), targetId, cancellationToken);
    }

    public Task<SavedContentPageResult> GetSavedContentAsync(
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetSavedContentAsync(trustedCaller.RequireUserId(), cursor, limit, cancellationToken);
    }

    public Task<UserSummaryPageResult> GetLikedUsersAsync(
        long targetId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetLikedUsersAsync(trustedCaller.RequireUserId(), targetId, cursor, limit, cancellationToken);
    }

    public Task<UserSummaryPageResult> GetStoryViewersAsync(
        long storyId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetStoryViewersAsync(trustedCaller.RequireUserId(), storyId, cursor, limit, cancellationToken);
    }

    public Task<UserSummaryPageResult> GetTaggedUsersAsync(
        long postId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetTaggedUsersAsync(trustedCaller.RequireUserId(), postId, cursor, limit, cancellationToken);
    }

    public Task<UserSummaryPageResult> GetMentionedUsersAsync(
        long sourceId,
        string? cursor,
        int limit,
        [Service] ISocialReadModelService readModels,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        return readModels.GetMentionedUsersAsync(trustedCaller.RequireUserId(), sourceId, cursor, limit, cancellationToken);
    }

    public async Task<AssociationPageResult> GetGroupJoinRequestsAsync(long groupId, string? cursor, int limit, [Service] IAssociationService associationService, [Service] IGroupGraphService groupGraphService, [Service] ITrustedCallerAccessor trustedCaller, CancellationToken cancellationToken)
    {
        var viewerId = trustedCaller.RequireUserId();
        if (!await groupGraphService.IsAdminAsync(viewerId, groupId, cancellationToken))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetCode("FORBIDDEN")
                    .SetMessage("Group administrator permission is required.")
                    .Build());
        }

        return await associationService.RetrieveAssociationAsync(groupId, GraphAssociationType.HaveGroupJoinRequest, cursor, limit, cancellationToken);
    }

    public Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        long userId,
        int limit,
        [Service] ICandidateService candidateService,
        [Service] ITrustedCallerAccessor trustedCaller,
        CancellationToken cancellationToken)
    {
        trustedCaller.RequireUserId(userId);
        return candidateService.GetReelCandidatesAsync(userId, limit, cancellationToken);
    }

    private static void EnsureBatchSize(int count)
    {
        if (count > MaxBatchLookupIds)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetCode("BAD_USER_INPUT")
                    .SetMessage($"At most {MaxBatchLookupIds} IDs can be requested.")
                    .Build());
        }
    }

    private static Task<bool> IsBlockedAsync(
        long viewerId,
        long userId,
        IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return viewerId == userId
            ? Task.FromResult(false)
            : IsBlockedCoreAsync(viewerId, userId, associationService, cancellationToken);
    }

    private static async Task<bool> IsBlockedCoreAsync(
        long viewerId,
        long userId,
        IAssociationService associationService,
        CancellationToken cancellationToken)
    {
        return await associationService.HasAssociationAsync(viewerId, GraphAssociationType.Blocked, userId, cancellationToken) ||
               await associationService.HasAssociationAsync(viewerId, GraphAssociationType.BlockedBy, userId, cancellationToken);
    }

    private static long PostId(IHomePostResult post) => post switch
    {
        FeedPostDetailResult feedPost => feedPost.Id,
        GroupPostDetailResult groupPost => groupPost.Id,
        _ => throw new InvalidOperationException("Unsupported profile post type.")
    };

    private static string AdvanceCursor(string? cursor, int processed)
    {
        var start = long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
        return (start + processed).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<GroupMembershipPageResult> GetGroupMembershipPageAsync(
        long userId,
        short associationType,
        string? cursor,
        int limit,
        IAssociationService associationService,
        IGroupGraphService groupGraphService,
        CancellationToken cancellationToken)
    {
        var page = await associationService.RetrieveAssociationAsync(
            userId,
            associationType,
            cursor,
            Math.Clamp(limit, 1, 50),
            cancellationToken);
        var groups = new List<GroupResult>(page.items.Count);
        foreach (var edge in page.items)
        {
            var group = await groupGraphService.GetGroupAsync(edge.id2, cancellationToken);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        return new GroupMembershipPageResult(groups, page.nextCursor, page.nextCursor is not null);
    }
}
