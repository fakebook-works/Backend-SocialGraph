namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public sealed class UserGraphService : IUserGraphService
{
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;
    private readonly IBillingClient _billingClient;

    public UserGraphService(
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient,
        IBillingClient billingClient)
    {
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
        _billingClient = billingClient;
    }

    public async Task<UserProfileResult> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default)
    {
        var user = await _objectService.AddObjectAsync(
            GraphObjectType.User,
            GraphJson.UserJson(input.Name, input.Gender, input.Birthdate, input.Location, input.Avatar),
            cancellationToken);

        await _externalServiceClient.CreateUserAsync(user.id, input.Email, input.Password, input.Name, cancellationToken);
        return (await GetProfileAsync(user.id, cancellationToken))!;
    }

    public async Task<UserProfileResult?> UpdateUserAsync(UpdateUserInput input, CancellationToken cancellationToken = default)
    {
        var patch = GraphJson.PatchJson(
            ("avatar", input.Avatar),
            ("name", input.Name),
            ("bio", input.Bio),
            ("gender", input.Gender is null ? null : input.Gender.Value ? 1 : 0),
            ("birthdate", input.Birthdate),
            ("location", input.Location),
            ("privacy", input.Privacy));

        var updated = await _objectService.UpdateObjectAsync(input.Id, GraphObjectType.User, patch, cancellationToken);
        if (updated is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Name))
        {
            await _externalServiceClient.UpdateSearchIndexAsync(input.Id, "user", input.Name, cancellationToken);
        }

        return await GetProfileAsync(input.Id, cancellationToken);
    }

    public async Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await _associationService.DeleteObjectAssociationsAsync(userId, cancellationToken);
        var deleted = await _objectService.DeleteObjectAsync(userId, cancellationToken);
        if (deleted)
        {
            await _externalServiceClient.DeleteUserAsync(userId, cancellationToken);
        }

        return deleted;
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
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "gender"),
            GraphJson.String(data, "birthdate"),
            GraphJson.String(data, "location"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            await _billingClient.IsVerifiedAsync(userId, cancellationToken),
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
            var media = await _objectService.AddObjectAsync(
                GraphObjectType.Media,
                GraphJson.MediaJson(GraphMediaType.Photo, originalUrl),
                cancellationToken);

            await _associationService.AddAssociationAsync(userId, GraphAssociationType.Owned, media.id, cancellationToken);
        }

        var updated = await _objectService.UpdateObjectAsync(
            userId,
            GraphObjectType.User,
            GraphJson.PatchJson(("avatar", avatarUrl)),
            cancellationToken);

        return updated is null ? null : await GetProfileAsync(userId, cancellationToken);
    }

    public Task<bool> SendFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        return NotifyAndReturnTrueAsync(requesterId, receiverId, ExternalNotificationAction.FriendRequest, requesterId, cancellationToken);
    }

    public async Task<bool> AcceptFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default)
    {
        var result = await _associationService.AddAssociationAsync(requesterId, GraphAssociationType.Friend, receiverId, cancellationToken);
        await _externalServiceClient.NotifyAsync(receiverId, requesterId, ExternalNotificationAction.FriendAccept, receiverId, null, cancellationToken);
        return result;
    }

    public Task<bool> FollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.AddAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken);
    }

    public Task<bool> UnfollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(followerId, GraphAssociationType.Followed, targetUserId, cancellationToken);
    }

    public async Task<bool> BlockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        await _associationService.DeleteOneAssociationAsync(blockerId, GraphAssociationType.Friend, blockedUserId, cancellationToken);
        await _associationService.DeleteOneAssociationAsync(blockerId, GraphAssociationType.Followed, blockedUserId, cancellationToken);
        await _associationService.DeleteOneAssociationAsync(blockedUserId, GraphAssociationType.Followed, blockerId, cancellationToken);
        return await _associationService.AddAssociationAsync(blockerId, GraphAssociationType.Blocked, blockedUserId, cancellationToken);
    }

    public Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(blockerId, GraphAssociationType.Blocked, blockedUserId, cancellationToken);
    }

    private async Task<bool> NotifyAndReturnTrueAsync(long creatorId, long receiverId, short actionType, long? objectId, CancellationToken cancellationToken)
    {
        await _externalServiceClient.NotifyAsync(creatorId, receiverId, actionType, objectId, null, cancellationToken);
        return true;
    }
}
