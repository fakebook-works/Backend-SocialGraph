namespace SocialGraph.Api.Contracts;

public sealed record MessagingPermissionCheckRequest(
    long ActorUserId,
    IReadOnlyCollection<long>? TargetUserIds,
    string? Action);

public sealed record MessagingPermissionDecision(
    long TargetUserId,
    bool Allowed,
    bool IsFriend,
    bool BlockedEitherDirection,
    string? Reason);

public sealed record MessagingPermissionCheckResponse(
    IReadOnlyList<MessagingPermissionDecision> Results);

