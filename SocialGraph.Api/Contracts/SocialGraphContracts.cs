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

public sealed record CreateStoryInput(long AuthorId, string Content, MediaInput? Media, long? SharedSourceId = null);

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

public sealed record UserSummaryResult(
    long Id,
    string Name,
    string Avatar,
    string Verify,
    bool IsVerified);

public sealed record GroupSummaryResult(
    long Id,
    string Name,
    string Avatar,
    string Background,
    int Privacy);

public interface IStorySharedSourceResult;

[GraphQLName("FeedPostSharedSource")]
public sealed record FeedPostSharedSourceResult(
    long Id,
    string Content,
    int Privacy,
    string Create,
    UserSummaryResult? Author,
    IReadOnlyList<MediaResult> Media) : IStorySharedSourceResult;

[GraphQLName("GroupPostSharedSource")]
public sealed record GroupPostSharedSourceResult(
    long Id,
    string Content,
    int Privacy,
    string Create,
    UserSummaryResult? Author,
    GroupSummaryResult? Group,
    IReadOnlyList<MediaResult> Media) : IStorySharedSourceResult;

[GraphQLName("ReelSharedSource")]
public sealed record ReelSharedSourceResult(
    long Id,
    string Content,
    string Create,
    UserSummaryResult? Author,
    IReadOnlyList<MediaResult> Media) : IStorySharedSourceResult;

[UnionType("HomeStory")]
public interface IHomeStoryResult;

[GraphQLName("NormalStory")]
public sealed record NormalStoryResult(
    long Id,
    string Content,
    string Create,
    string Expire,
    IReadOnlyList<MediaResult> Media) : IHomeStoryResult;

[GraphQLName("FeedPostShareStory")]
public sealed record FeedPostShareStoryResult(
    long Id,
    string Content,
    string Create,
    string Expire,
    FeedPostSharedSourceResult SharedSource) : IHomeStoryResult;

[GraphQLName("GroupPostShareStory")]
public sealed record GroupPostShareStoryResult(
    long Id,
    string Content,
    string Create,
    string Expire,
    GroupPostSharedSourceResult SharedSource) : IHomeStoryResult;

[GraphQLName("ReelShareStory")]
public sealed record ReelShareStoryResult(
    long Id,
    string Content,
    string Create,
    string Expire,
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
