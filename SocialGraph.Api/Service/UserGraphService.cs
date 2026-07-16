namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class UserGraphService : IUserGraphService
{
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;
    private readonly MyDbContext? _dbContext;

    public UserGraphService(
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient,
        MyDbContext? dbContext = null)
    {
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
        _dbContext = dbContext;
    }

    public async Task<CreateUserPayload> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default)
    {
        SocialGraphObjectResult? user = null;
        await using var transaction = await BeginTransactionAsync(cancellationToken);

        try
        {
            user = await _objectService.AddObjectAsync(
                GraphObjectType.User,
                GraphJson.UserJson(input.Name, input.Gender, input.Birthdate, input.Location),
                cancellationToken);
            await _externalServiceClient.CreateUserAsync(
                user.id,
                input.Email,
                input.Password,
                input.Name,
                input.Birthdate,
                input.Gender,
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new CreateUserPayload(true, user.id, "User created; downstream provisioning queued.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollbackCreateAsync(transaction, user, transactional: transaction is not null);
            throw;
        }
        catch (Exception)
        {
            await RollbackCreateAsync(transaction, user, transactional: transaction is not null);
            return new CreateUserPayload(false, null, "User creation could not be queued safely.");
        }
    }

    public async Task<UserProfileResult?> UpdateUserAsync(UpdateUserInput input, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var patch = GraphJson.PatchJson(
            ("avatar", input.Avatar),
            ("background", input.Background),
            ("name", input.Name),
            ("bio", input.Bio),
            ("gender", input.Gender is null ? null : input.Gender.Value ? 1 : 0),
            ("birthdate", input.Birthdate),
            ("location", input.Location),
            ("privacy", input.Privacy));

        try
        {
            var updated = await _objectService.UpdateObjectAsync(input.Id, GraphObjectType.User, patch, cancellationToken);
            if (updated is null)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return null;
            }

            if (!string.IsNullOrWhiteSpace(input.Name))
            {
                await _externalServiceClient.UpdateSearchIndexAsync(input.Id, "user", input.Name, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, input.Id);
            throw;
        }

        return await GetProfileAsync(input.Id, cancellationToken);
    }

    public async Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            await _associationService.DeleteObjectAssociationsAsync(userId, cancellationToken);
            var deleted = await _objectService.DeleteObjectAsync(userId, cancellationToken);
            if (deleted)
            {
                await _externalServiceClient.DeleteUserAsync(userId, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return deleted;
        }
        catch
        {
            await RollbackAndInvalidateAsync(transaction, userId);
            throw;
        }
    }

    public async Task<UserProfileResult?> GetProfileAsync(long userId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (item is null || item.otype != GraphObjectType.User)
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        return new UserProfileResult(
            item.id,
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "gender"),
            GraphJson.String(data, "birthdate"),
            GraphJson.String(data, "location"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            GraphJson.String(data, "verify"),
            IsVerifyActive(data),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.Friend, cancellationToken),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.FollowedBy, cancellationToken),
            await _associationService.CountAssociationAsync(userId, GraphAssociationType.Followed, cancellationToken));
    }

    public async Task<UserProfileResult?> ChangeUserAvatarAsync(
        long userId,
        string avatarUrl,
        string? originalUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (currentUser is null || currentUser.otype != GraphObjectType.User)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(originalUrl))
        {
            await AddOwnedPhotoAsync(userId, originalUrl, cancellationToken);
        }

        var updated = await _objectService.UpdateObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJson(("avatar", avatarUrl)),
            cancellationToken);

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public async Task<UserProfileResult?> SetUserVerifyAsync(
        long userId,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
    {
        var verify = expiresAt?.ToUniversalTime().ToString("O") ?? "";
        var updated = await _objectService.UpdateSystemObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJson(("verify", verify)),
            cancellationToken);

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public async Task<UserProfileResult?> ChangeUserBackgroundAsync(
        long userId,
        string backgroundUrl,
        string? originalUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentUser = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        if (currentUser is null || currentUser.otype != GraphObjectType.User)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(originalUrl))
        {
            await AddOwnedPhotoAsync(userId, originalUrl, cancellationToken);
        }

        var updated = await _objectService.UpdateObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJson(("background", backgroundUrl)),
            cancellationToken);

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public async Task<bool> SendFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(requesterId, receiverId, cancellationToken) ||
            await IsBlockedEitherWayAsync(requesterId, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.Friend, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.FriendRequest, receiverId, cancellationToken) ||
            await _associationService.HasAssociationAsync(receiverId, GraphAssociationType.FriendRequest, requesterId, cancellationToken))
        {
            return false;
        }

        await _associationService.AddAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
        await _externalServiceClient.NotifyAsync(
            requesterId,
            receiverId,
            ExternalNotificationAction.FriendRequest,
            requesterId,
            null,
            cancellationToken);
        return true;
    }

    public Task<bool> CancelFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
    }

    public async Task<bool> AcceptFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(requesterId, receiverId, cancellationToken) ||
            await IsBlockedEitherWayAsync(requesterId, receiverId, cancellationToken) ||
            !await _associationService.HasAssociationAsync(requesterId, GraphAssociationType.FriendRequest, receiverId, cancellationToken))
        {
            return false;
        }

        var result = await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(requesterId, GraphAssociationType.FriendRequest, receiverId, false),
                new(requesterId, GraphAssociationType.Followed, receiverId, false),
                new(receiverId, GraphAssociationType.Followed, requesterId, false),
                new(requesterId, GraphAssociationType.Friend, receiverId, true)
            },
            cancellationToken);
        if (result)
        {
            await _externalServiceClient.NotifyAsync(
                receiverId,
                requesterId,
                ExternalNotificationAction.FriendAccept,
                receiverId,
                null,
                cancellationToken);
        }

        return result;
    }

    public Task<bool> RejectFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            requesterId,
            GraphAssociationType.FriendRequest,
            receiverId,
            cancellationToken);
    }

    public Task<bool> UnfriendAsync(long userId, long friendId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(
            userId,
            GraphAssociationType.Friend,
            friendId,
            cancellationToken);
    }

    public async Task<bool> FollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(followerId, targetUserId, cancellationToken) ||
            await IsBlockedEitherWayAsync(followerId, targetUserId, cancellationToken) ||
            await _associationService.HasAssociationAsync(followerId, GraphAssociationType.Friend, targetUserId, cancellationToken) ||
            await _associationService.HasAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken))
        {
            return false;
        }

        return await _associationService.AddAssociationAsync(
            followerId,
            GraphAssociationType.Followed,
            targetUserId,
            cancellationToken);
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken);
    }

    public async Task<bool> BlockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        if (!await CanCreateUserRelationshipAsync(blockerId, blockedUserId, cancellationToken))
        {
            return false;
        }

        return await _associationService.ApplyMutationsAsync(
            new AssociationMutation[]
            {
                new(blockerId, GraphAssociationType.Friend, blockedUserId, false),
                new(blockerId, GraphAssociationType.FriendRequest, blockedUserId, false),
                new(blockedUserId, GraphAssociationType.FriendRequest, blockerId, false),
                new(blockerId, GraphAssociationType.Followed, blockedUserId, false),
                new(blockedUserId, GraphAssociationType.Followed, blockerId, false),
                new(blockerId, GraphAssociationType.Blocked, blockedUserId, true)
            },
            cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(blockerId, GraphAssociationType.Blocked, blockedUserId, cancellationToken);
    }

    private async Task<bool> CanCreateUserRelationshipAsync(
        long userId,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || targetUserId <= 0 || userId == targetUserId)
        {
            return false;
        }

        var source = await _objectService.RetrieveObjectAsync(userId, cancellationToken);
        var target = await _objectService.RetrieveObjectAsync(targetUserId, cancellationToken);
        return source?.otype == GraphObjectType.User && target?.otype == GraphObjectType.User;
    }

    private async Task<bool> IsBlockedEitherWayAsync(
        long userId,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        return await _associationService.HasAssociationAsync(userId, GraphAssociationType.Blocked, targetUserId, cancellationToken) ||
               await _associationService.HasAssociationAsync(userId, GraphAssociationType.BlockedBy, targetUserId, cancellationToken);
    }

    private async Task AddOwnedPhotoAsync(long ownerId, string url, CancellationToken cancellationToken)
    {
        var media = await _objectService.AddObjectAsync(
            GraphObjectType.Media,
            GraphJson.MediaJson(GraphMediaType.Photo, url),
            cancellationToken);

        await _associationService.AddAssociationAsync(ownerId, GraphAssociationType.Owned, media.id, cancellationToken);
    }

    private static bool IsVerifyActive(System.Text.Json.Nodes.JsonObject data)
    {
        var raw = GraphJson.String(data, "verify");
        return DateTimeOffset.TryParse(raw, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_dbContext is null ||
            _dbContext.Database.CurrentTransaction is not null ||
            !_dbContext.Database.IsRelational())
        {
            return null;
        }

        return await _dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    private async Task RollbackCreateAsync(
        IDbContextTransaction? transaction,
        SocialGraphObjectResult? user,
        bool transactional)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        if (user is null)
        {
            return;
        }

        if (transactional)
        {
            await _objectService.InvalidateObjectCacheAsync(user.id);
        }
        else
        {
            await _objectService.DeleteObjectAsync(user.id, CancellationToken.None);
        }
    }

    private async Task RollbackAndInvalidateAsync(IDbContextTransaction? transaction, long objectId)
    {
        if (transaction is null)
        {
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
        await _objectService.InvalidateObjectCacheAsync(objectId);
    }
}
