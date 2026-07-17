namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface ISocialReadModelService
{
    Task<UserRelationshipStateResult?> GetUserRelationshipStateAsync(long viewerId, long userId, CancellationToken cancellationToken = default);
    Task<GroupViewerStateResult?> GetGroupViewerStateAsync(long viewerId, long groupId, CancellationToken cancellationToken = default);
    Task<GroupMembershipPageResult> GetPendingGroupJoinsAsync(long userId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<UserSummaryPageResult> GetGroupMembersAsync(long viewerId, long groupId, string? cursor, int limit, bool admins, CancellationToken cancellationToken = default);
    Task<GroupPostPageResult> GetGroupPostsAsync(long viewerId, long groupId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<GroupPostPageResult> GetGroupUserPostsAsync(long viewerId, long groupId, long userId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<PhotoPageResult> GetUserPhotosAsync(long viewerId, long userId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<PhotoPageResult> GetGroupPhotosAsync(long viewerId, long groupId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<PhotoPageResult> GetGroupUserPhotosAsync(long viewerId, long groupId, long userId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<PhotoPageResult> GetMyFeedPhotoCandidatesAsync(long viewerId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<PhotoPageResult> GetGroupPhotoCandidatesAsync(long viewerId, long groupId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ProfileReelPageResult> GetLikedReelsAsync(long viewerId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ProfileReelPageResult> GetSharedReelsAsync(long viewerId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ProfileReelPageResult> GetWatchedReelsAsync(long viewerId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<CommentPageResult> GetCommentsAsync(long viewerId, long targetId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<ContentEngagementResult?> GetEngagementAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
    Task<SavedContentPageResult> GetSavedContentAsync(long viewerId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<UserSummaryPageResult> GetLikedUsersAsync(long viewerId, long targetId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<UserSummaryPageResult> GetStoryViewersAsync(long viewerId, long storyId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<UserSummaryPageResult> GetTaggedUsersAsync(long viewerId, long postId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<UserSummaryPageResult> GetMentionedUsersAsync(long viewerId, long sourceId, string? cursor, int limit, CancellationToken cancellationToken = default);
    Task<bool> CanViewTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> CanCommentTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> CanSaveTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> CanWatchTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
    Task<bool> CanShareTargetAsync(long viewerId, long targetId, CancellationToken cancellationToken = default);
}
