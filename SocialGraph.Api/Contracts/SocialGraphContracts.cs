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

public sealed record CreateFeedPostInput(long AuthorId, string Content, int Privacy, IReadOnlyList<MediaInput>? Media);

public sealed record CreateGroupPostInput(long AuthorId, long GroupId, string Content, IReadOnlyList<MediaInput>? Media);

public sealed record UpdatePostInput(long Id, int Privacy);

public sealed record CreateCommentInput(long AuthorId, long TargetId, string Content);

public sealed record CreateStoryInput(long AuthorId, string Content, MediaInput? Media);

public sealed record CreateNormalStoryInput(long AuthorId, string Content, MediaInput? Media);

public sealed record CreateShareStoryInput(long AuthorId, string Content, long SharedSourceId);

public sealed record DeleteStoryInput(long AuthorId, long StoryId);

public sealed record DeleteStoryPayload(bool Success, string? Message = null);

public sealed record StoryCleanupPayload(int Deleted);

public sealed record CreateReelInput(long AuthorId, string Content, MediaInput? Media);

public sealed record SharePostInput(long AuthorId, long SourceId, string Content, int Privacy);

public sealed record OperationResult(bool Success, string? Message = null);

public sealed record MediaResult(long Id, int Type, string Url);

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
    string Verify,
    bool IsVerified,
    long FriendCount,
    long FollowerCount,
    long FollowingCount);

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

public sealed record PostViewerRelationResult(
    bool? IsFriend,
    bool? IsFollow,
    bool? IsParticipant);

public sealed record PostDetailResult(
    long Id,
    short Type,
    string Content,
    int Privacy,
    string Create,
    UserSummaryResult Author,
    GroupSummaryResult? Group,
    PostViewerRelationResult ViewerRelation,
    IReadOnlyList<MediaResult> Media);

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
