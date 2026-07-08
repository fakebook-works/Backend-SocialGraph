namespace SocialGraph.Api.Contracts;

public sealed record CreateUserInput(
    string Name,
    bool Gender,
    string Birthdate,
    string Location,
    string Email,
    string Password,
    string? Avatar = null);

public sealed record UpdateUserInput(
    long Id,
    string? Avatar,
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
    string? Avatar = null);

public sealed record UpdateGroupInput(
    long Id,
    string? Avatar,
    string? Name,
    string? Bio,
    int? Privacy);

public sealed record MediaInput(int Type, string Url);

public sealed record PrepareUploadInput(long OwnerId, string FileName, int Type);

public sealed record CreateFeedPostInput(long AuthorId, string Content, int Privacy, IReadOnlyList<MediaInput>? Media);

public sealed record CreateGroupPostInput(long AuthorId, long GroupId, string Content, int Privacy, IReadOnlyList<MediaInput>? Media);

public sealed record UpdatePostInput(long Id, int Privacy);

public sealed record CreateCommentInput(long AuthorId, long TargetId, string Content);

public sealed record CreateStoryInput(long AuthorId, string Content, DateTime Expire, IReadOnlyList<MediaInput>? Media);

public sealed record CreateReelInput(long AuthorId, string Content, IReadOnlyList<MediaInput>? Media);

public sealed record SharePostInput(long AuthorId, long SourceId, string Content, int Privacy);

public sealed record OperationResult(bool Success, string? Message = null);

public sealed record MediaResult(long Id, int Type, string Url);

public sealed record UploadUrlResult(long MediaId, string TemporaryUrl, string PermanentUrl);

public sealed record UserProfileResult(
    long Id,
    string Avatar,
    string Name,
    string Bio,
    int Gender,
    string Birthdate,
    string Location,
    int Privacy,
    string Create,
    bool IsVerified,
    long FriendCount,
    long FollowerCount,
    long FollowingCount);

public sealed record GroupResult(
    long Id,
    string Avatar,
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

public sealed record CandidateItemResult(
    long Id,
    long AuthorId,
    string Source,
    string CreatedAt,
    double BoostMultiplier);

public sealed record EntitlementResult(string Type, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string> Metadata);
