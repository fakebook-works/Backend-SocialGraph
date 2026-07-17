namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface IUserGraphService
{
    Task<CreateUserPayload> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default);
    Task<UserProfileResult?> UpdateUserAsync(UpdateUserInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default);
    Task<UserProfileResult?> GetProfileAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserProfileResult>> GetProfilesForViewerAsync(long viewerId, IReadOnlyCollection<long> userIds, CancellationToken cancellationToken = default);
    Task<UserProfileResult?> ChangeUserAvatarAsync(long userId, string avatarUrl, string? originalUrl = null, int privacy = 0, CancellationToken cancellationToken = default);
    Task<UserProfileResult?> ChangeUserAvatarAsync(long userId, string avatarUrl, string? originalUrl, CancellationToken cancellationToken);
    Task<UserProfileResult?> ChangeUserBackgroundAsync(long userId, string backgroundUrl, string? originalUrl = null, int privacy = 0, CancellationToken cancellationToken = default);
    Task<UserProfileResult?> ChangeUserBackgroundAsync(long userId, string backgroundUrl, string? originalUrl, CancellationToken cancellationToken);
    Task<UserProfileResult?> SetUserVerifyAsync(long userId, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default);
    Task<bool> SendFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default);
    Task<bool> CancelFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default);
    Task<bool> AcceptFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default);
    Task<bool> RejectFriendRequestAsync(long requesterId, long receiverId, CancellationToken cancellationToken = default);
    Task<bool> UnfriendAsync(long userId, long friendId, CancellationToken cancellationToken = default);
    Task<bool> FollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default);
    Task<bool> UnfollowUserAsync(long followerId, long targetUserId, CancellationToken cancellationToken = default);
    Task<bool> BlockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default);
    Task<bool> UnblockUserAsync(long blockerId, long blockedUserId, CancellationToken cancellationToken = default);
}
