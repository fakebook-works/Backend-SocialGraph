namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class MessagingPermissionService : IMessagingPermissionService
{
    public const int MaxTargets = 100;
    private static readonly ISet<string> SupportedActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "CREATE_DIRECT",
        "SEND_DIRECT",
        "ADD_GROUP_MEMBERS"
    };

    private readonly MyDbContext _dbContext;

    public MessagingPermissionService(MyDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MessagingPermissionCheckResponse> CheckAsync(
        MessagingPermissionCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var targetIds = request.TargetUserIds!.ToArray();
        var requestedIds = targetIds.Append(request.ActorUserId).Distinct().ToArray();
        var existingUsers = (await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => requestedIds.Contains(item.id) && item.otype == GraphObjectType.User)
            .Select(item => item.id)
            .ToListAsync(cancellationToken))
            .ToHashSet();
        if (!existingUsers.Contains(request.ActorUserId))
        {
            throw new ArgumentException("actorUserId must reference an existing user.", nameof(request));
        }

        var relations = await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == request.ActorUserId &&
                targetIds.Contains(item.id2) &&
                (item.atype == GraphAssociationType.Friend ||
                 item.atype == GraphAssociationType.Blocked ||
                 item.atype == GraphAssociationType.BlockedBy))
            .ToListAsync(cancellationToken);
        var friends = relations.Where(item => item.atype == GraphAssociationType.Friend).Select(item => item.id2).ToHashSet();
        var blocked = relations.Where(item => item.atype is GraphAssociationType.Blocked or GraphAssociationType.BlockedBy).Select(item => item.id2).ToHashSet();
        var decisions = targetIds.Select(targetId =>
        {
            var exists = existingUsers.Contains(targetId);
            var blockedEitherDirection = blocked.Contains(targetId);
            var isFriend = exists && friends.Contains(targetId) && !blockedEitherDirection;
            var allowed = targetId != request.ActorUserId && exists && isFriend && !blockedEitherDirection;
            var reason = allowed
                ? null
                : targetId == request.ActorUserId
                    ? "SELF_TARGET"
                    : !exists
                        ? "USER_NOT_FOUND"
                        : blockedEitherDirection
                            ? "BLOCKED"
                            : "NOT_FRIENDS";
            return new MessagingPermissionDecision(targetId, allowed, isFriend, blockedEitherDirection, reason);
        }).ToArray();

        return new MessagingPermissionCheckResponse(decisions);
    }

    private static void Validate(MessagingPermissionCheckRequest request)
    {
        if (request.ActorUserId <= 0)
        {
            throw new ArgumentException("actorUserId must be positive.", nameof(request));
        }

        if (request.TargetUserIds is null || request.TargetUserIds.Count is < 1 or > MaxTargets)
        {
            throw new ArgumentException($"targetUserIds must contain between 1 and {MaxTargets} IDs.", nameof(request));
        }

        if (request.TargetUserIds.Any(item => item <= 0) || request.TargetUserIds.Distinct().Count() != request.TargetUserIds.Count)
        {
            throw new ArgumentException("targetUserIds must contain unique positive IDs.", nameof(request));
        }

        if (request.Action is null || !SupportedActions.Contains(request.Action))
        {
            throw new ArgumentException("action must be CREATE_DIRECT, SEND_DIRECT, or ADD_GROUP_MEMBERS.", nameof(request));
        }
    }
}

