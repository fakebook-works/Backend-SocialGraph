namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface IGroupGraphService
{
    Task<GroupResult> CreateGroupAsync(CreateGroupInput input, CancellationToken cancellationToken = default);
    Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, CancellationToken cancellationToken = default);
    Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default);
    Task<GroupResult?> GetGroupAsync(long groupId, CancellationToken cancellationToken = default);
    Task<GroupResult?> ChangeGroupAvatarAsync(long groupId, string avatarUrl, CancellationToken cancellationToken = default);
    Task<GroupResult?> ChangeGroupBackgroundAsync(long groupId, string backgroundUrl, string? originalUrl = null, CancellationToken cancellationToken = default);
    Task<VisitedGroupPageResult> GetVisitedGroupsAsync(long userId, int limit, string? cursor, CancellationToken cancellationToken = default);
    Task<bool> RecordGroupVisitAsync(long userId, long groupId, CancellationToken cancellationToken = default);
    Task<bool> AddMemberAsync(long groupId, long userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveMemberAsync(long groupId, long userId, CancellationToken cancellationToken = default);
    Task<bool> AddAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default);
}
