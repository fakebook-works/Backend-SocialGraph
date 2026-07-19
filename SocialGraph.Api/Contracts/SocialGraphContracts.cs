namespace SocialGraph.Api.Contracts;

using HotChocolate;
using HotChocolate.Types;

public sealed record CreateUserInput(
    string Name,
    bool Gender,
    string Birthdate,
    string Location,
    string Email,
    string Password);

public sealed record CreateUserPayload(bool Success, long? UserId, string? Message);

public sealed record UpdateUserInput(
    long Id,
    string? Avatar,
    string? Background,
    string? Name,
    string? Bio,
    bool? Gender,
    string? Birthdate,
    string? Location,
    int? Privacy);

public sealed record CreateGroupInput(
    long CreatorId,
    string Name,
    string? Bio,
    int Privacy,
    string? Avatar = null,
    string? Background = null);

public sealed record UpdateGroupInput(
    long Id,
    string? Avatar,
    string? Background,
    string? Name,
    string? Bio,
    int? Privacy);

public sealed record MediaInput(int Type, string Url);

public sealed record CreateFeedPostInput(
    long AuthorId,
    string Content,
    int Privacy,
    IReadOnlyList<MediaInput>? Media,
    IReadOnlyList<long>? TaggedUserIds = null,
    IReadOnlyList<long>? MentionedUserIds = null);

public sealed record CreateGroupPostInput(
    long AuthorId,
    long GroupId,
    string Content,
    IReadOnlyList<MediaInput>? Media,
    IReadOnlyList<long>? MentionedUserIds = null);

public sealed record UpdatePostInput(
    long Id,
    int? Privacy = null,
    string? Content = null,
    IReadOnlyList<MediaInput>? Media = null);

public sealed record CreateCommentInput(long AuthorId, long TargetId, string Content);

public sealed record CreateNormalStoryInput(long AuthorId, string Content, MediaInput? Media);

public sealed record CreateShareStoryInput(long AuthorId, string Content, long SharedSourceId);

public sealed record DeleteStoryInput(long AuthorId, long StoryId);

public sealed record DeleteStoryPayload(bool Success, string? Message = null);

public sealed record StoryCleanupPayload(int Deleted);

public sealed record CreateReelInput(long AuthorId, string Content, MediaInput? Media);

public sealed record SharePostInput(long AuthorId, long SourceId, string Content, int Privacy);

public sealed record OperationResult(bool Success, string? Message = null);

public sealed record MediaResult(long Id, int Type, string Url);

public sealed record MentionUserResult(
    long UserId,
    string Name,
    bool Available);

public sealed record UserProfileResult(
    long Id,
    string Avatar,
    string Background,
    string Name,
    string Bio,
    int Gender,
    string Birthdate,
    string Location,
    int Privacy,
    string Create,
    string? Verify,
    bool IsVerified,
    long FriendCount,
    long FollowerCount,
    long FollowingCount);

public sealed record FriendSuggestionResult(
    UserProfileResult Profile,
    int MutualFriendCount,
    IReadOnlyList<UserSummaryResult> MutualFriends);

public sealed record GroupResult(
    long Id,
    string Avatar,
    string Background,
    string Name,
    string Bio,
    int Privacy,
    string Create,
    long MemberCount,
    long AdminCount);

public sealed record ContentResult(
    long Id,
    short Type,
    string Content,
    int Privacy,
    string Create,
    long AuthorId,
    IReadOnlyList<MediaResult> Media);

[UnionType("HomePost")]
public interface IHomePostResult;

[GraphQLName("FeedPostDetail")]
public sealed record FeedPostDetailResult(
    long Id,
    short Type,
    string Content,
    int Privacy,
    string Create,
    PostAuthorResult Author,
    IReadOnlyList<MediaResult> Media,
    SharedPostSourceResult? SharedSource = null,
    IReadOnlyList<MentionUserResult>? Mentions = null,
    IReadOnlyList<UserSummaryResult>? TaggedUsers = null) : IHomePostResult;

[GraphQLName("GroupPostDetail")]
public sealed record GroupPostDetailResult(
    long Id,
    short Type,
    string Content,
    int Privacy,
    string Create,
    PostAuthorResult Author,
    PostGroupResult Group,
    IReadOnlyList<MediaResult> Media,
    IReadOnlyList<MentionUserResult>? Mentions = null) : IHomePostResult;

[GraphQLName("RecommendationItem")]
public sealed record RecommendationItemResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long PostId);

[GraphQLName("PostAuthor")]
public sealed record PostAuthorResult(
    long Id,
    string Name,
    string Avatar,
    bool IsVerified,
    bool CanFollow);

[GraphQLName("PostGroup")]
public sealed record PostGroupResult(
    long Id,
    string Name,
    string Avatar,
    bool CanJoin);

public sealed record SharedPostSourceResult(
    long Id,
    bool IsAvailable,
    short? Type,
    string? Content,
    UserSummaryResult? Author,
    IReadOnlyList<MediaResult> Media,
    IReadOnlyList<MentionUserResult>? Mentions = null);

public sealed record UserSummaryResult(
    long Id,
    string Name,
    string Avatar,
    bool IsVerified);

public sealed record GroupSummaryResult(
    long Id,
    string Name,
    string Avatar);

public sealed record VisitedGroupResult(
    long Id,
    string Avatar,
    string Name);

public sealed record VisitedGroupPageResult(
    IReadOnlyList<VisitedGroupResult> Items,
    string? EndCursor,
    bool HasNextPage);

public interface IStorySharedSourceResult;

[GraphQLName("FeedPostSharedSource")]
public sealed record FeedPostSharedSourceResult(
    long Id,
    string Content,
    MediaResult? Media,
    UserSummaryResult? Author) : IStorySharedSourceResult;

[GraphQLName("ReelSharedSource")]
public sealed record ReelSharedSourceResult(
    long Id,
    string Content,
    MediaResult? Media,
    UserSummaryResult? Author) : IStorySharedSourceResult;

[UnionType("HomeStory")]
public interface IHomeStoryResult;

[GraphQLName("NormalStory")]
public sealed record NormalStoryResult(
    long Id,
    string Content,
    string Create,
    IReadOnlyList<MediaResult> Media) : IHomeStoryResult;

[GraphQLName("FeedPostShareStory")]
public sealed record FeedPostShareStoryResult(
    long Id,
    string Content,
    string Create,
    FeedPostSharedSourceResult SharedSource) : IHomeStoryResult;

[GraphQLName("ReelShareStory")]
public sealed record ReelShareStoryResult(
    long Id,
    string Content,
    string Create,
    ReelSharedSourceResult SharedSource) : IHomeStoryResult;

public sealed record HomeStoryBucketResult(
    UserSummaryResult Author,
    string LatestCreate,
    bool HasUnseen,
    IReadOnlyList<IHomeStoryResult> Stories);

public sealed record HomeStoryPageResult(
    IReadOnlyList<HomeStoryBucketResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record CandidateItemResult(
    long Id,
    long AuthorId,
    string Source,
    string CreatedAt);

public sealed record ProfilePostPageResult(
    IReadOnlyList<IHomePostResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record ProfileReelPageResult(
    IReadOnlyList<ContentResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record MediaPageResult(
    IReadOnlyList<MediaResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record PhotoItemResult(
    MediaResult Media,
    long ContentId,
    short ContentType,
    string Create,
    long AuthorId,
    long? GroupId);

public sealed record PhotoPageResult(
    IReadOnlyList<PhotoItemResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record GroupMembershipPageResult(
    IReadOnlyList<GroupResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record UserRelationshipStateResult(
    long UserId,
    bool IsSelf,
    bool IsFriend,
    bool IsFollowing,
    bool FollowsViewer,
    bool FriendRequestSent,
    bool FriendRequestReceived,
    bool IsBlocked,
    bool IsBlockedBy);

public sealed record GroupViewerStateResult(
    long GroupId,
    bool IsMember,
    bool IsAdmin,
    bool JoinRequestPending,
    bool CanViewPosts);

public sealed record UserSummaryPageResult(
    IReadOnlyList<UserSummaryResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record GroupPostPageResult(
    IReadOnlyList<GroupPostDetailResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record CommentThreadItemResult(
    long Id,
    string Content,
    string Create,
    UserSummaryResult Author,
    long LikeCount,
    long ReplyCount,
    bool ViewerHasLiked,
    IReadOnlyList<MentionUserResult>? Mentions = null);

public sealed record CommentPageResult(
    IReadOnlyList<CommentThreadItemResult> Items,
    string? EndCursor,
    bool HasNextPage);

public sealed record ContentEngagementResult(
    long TargetId,
    long LikeCount,
    long CommentCount,
    long ShareCount,
    bool ViewerHasLiked,
    bool ViewerHasSaved,
    bool ViewerHasWatched);

public sealed record SavedContentItemResult(
    long Id,
    short Type,
    IHomePostResult? Post,
    ContentResult? Reel);

public sealed record SavedContentPageResult(
    IReadOnlyList<SavedContentItemResult> Items,
    string? EndCursor,
    bool HasNextPage);

[GraphQLName("ReelRecommendationItem")]
public sealed record ReelRecommendationItemResult(
    [property: GraphQLType(typeof(NonNullType<IdType>))] long ReelId);
