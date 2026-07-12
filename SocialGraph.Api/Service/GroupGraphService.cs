namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;

public sealed class GroupGraphService : IGroupGraphService
{
    private readonly MyDbContext _dbContext;
    private readonly IObjectService _objectService;
    private readonly IAssociationService _associationService;
    private readonly IExternalServiceClient _externalServiceClient;

    public GroupGraphService(
        MyDbContext dbContext,
        IObjectService objectService,
        IAssociationService associationService,
        IExternalServiceClient externalServiceClient)
    {
        _dbContext = dbContext;
        _objectService = objectService;
        _associationService = associationService;
        _externalServiceClient = externalServiceClient;
    }

    public async Task<GroupResult> CreateGroupAsync(CreateGroupInput input, CancellationToken cancellationToken = default)
    {
        var group = await _objectService.AddObjectAsync(
            GraphObjectType.Group,
            GraphJson.GroupJson(input.Name, input.Bio, input.Privacy, input.Avatar, input.Background),
            cancellationToken);

        await _associationService.AddAssociationAsync(input.CreatorId, GraphAssociationType.Admin, group.id, cancellationToken);
        await _externalServiceClient.CreateSearchIndexAsync(group.id, "group", input.Name, cancellationToken);
        return (await GetGroupAsync(group.id, cancellationToken))!;
    }

    public async Task<GroupResult?> UpdateGroupAsync(UpdateGroupInput input, CancellationToken cancellationToken = default)
    {
        var updated = await _objectService.UpdateObjectAsync(
            input.Id,
            GraphObjectType.Group,
            GraphJson.PatchJson(("avatar", input.Avatar), ("background", input.Background), ("name", input.Name), ("bio", input.Bio), ("privacy", input.Privacy)),
            cancellationToken);

        if (updated is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Name))
        {
            await _externalServiceClient.UpdateSearchIndexAsync(input.Id, "group", input.Name, cancellationToken);
        }

        return await GetGroupAsync(input.Id, cancellationToken);
    }

    public async Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        await _associationService.DeleteObjectAssociationsAsync(groupId, cancellationToken);
        var deleted = await _objectService.DeleteObjectAsync(groupId, cancellationToken);
        if (deleted)
        {
            await _externalServiceClient.DeleteSearchIndexAsync(groupId, cancellationToken);
        }

        return deleted;
    }

    public async Task<GroupResult?> GetGroupAsync(long groupId, CancellationToken cancellationToken = default)
    {
        var item = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (item is null || item.otype != GraphObjectType.Group)
        {
            return null;
        }

        var data = GraphJson.ParseObject(item.data);
        return new GroupResult(
            item.id,
            GraphJson.String(data, "avatar"),
            GraphJson.String(data, "background"),
            GraphJson.String(data, "name"),
            GraphJson.String(data, "bio"),
            GraphJson.Int(data, "privacy"),
            GraphJson.String(data, "create"),
            await _associationService.CountAssociationAsync(groupId, GraphAssociationType.HaveMember, cancellationToken),
            await _associationService.CountAssociationAsync(groupId, GraphAssociationType.HaveAdmin, cancellationToken));
    }

    public async Task<GroupResult?> ChangeGroupAvatarAsync(long groupId, string avatarUrl, CancellationToken cancellationToken = default)
    {
        var updated = await _objectService.UpdateObjectAsync(groupId, GraphObjectType.Group, GraphJson.PatchJson(("avatar", avatarUrl)), cancellationToken);
        return updated is null ? null : await GetGroupAsync(groupId, cancellationToken);
    }

    public async Task<GroupResult?> ChangeGroupBackgroundAsync(
        long groupId,
        string backgroundUrl,
        string? originalUrl = null,
        CancellationToken cancellationToken = default)
    {
        var currentGroup = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (currentGroup is null || currentGroup.otype != GraphObjectType.Group)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(originalUrl))
        {
            await AddOwnedPhotoAsync(groupId, originalUrl, cancellationToken);
        }

        var updated = await _objectService.UpdateObjectAsync(
            groupId,
            GraphObjectType.Group,
            GraphJson.PatchJson(("background", backgroundUrl)),
            cancellationToken);

        return updated is null ? null : await GetGroupAsync(groupId, cancellationToken);
    }

    public async Task<VisitedGroupPageResult> GetVisitedGroupsAsync(
        long userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var page = await _associationService.RetrieveAssociationAsync(
            userId,
            GraphAssociationType.Visited,
            cursor,
            limit,
            cancellationToken);

        var items = new List<VisitedGroupResult>(page.items.Count);
        foreach (var edge in page.items)
        {
            var group = await _objectService.RetrieveObjectAsync(edge.id2, cancellationToken);
            if (group is null || group.otype != GraphObjectType.Group)
            {
                continue;
            }

            if (!await CanViewGroupAsync(userId, group, cancellationToken))
            {
                continue;
            }

            var data = GraphJson.ParseObject(group.data);
            items.Add(new VisitedGroupResult(
                group.id,
                GraphJson.String(data, "avatar"),
                GraphJson.String(data, "name")));
        }

        return new VisitedGroupPageResult(items, page.nextCursor, page.nextCursor is not null);
    }

    public async Task<bool> RecordGroupVisitAsync(
        long userId,
        long groupId,
        CancellationToken cancellationToken = default)
    {
        var group = await _objectService.RetrieveObjectAsync(groupId, cancellationToken);
        if (group is null || group.otype != GraphObjectType.Group)
        {
            return false;
        }

        if (!await CanViewGroupAsync(userId, group, cancellationToken))
        {
            return false;
        }

        return await _associationService.AddAssociationAsync(
            userId,
            GraphAssociationType.Visited,
            groupId,
            cancellationToken);
    }

    public Task<bool> AddMemberAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return _associationService.AddAssociationAsync(userId, GraphAssociationType.Member, groupId, cancellationToken);
    }

    public Task<bool> RemoveMemberAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Member, groupId, cancellationToken);
    }

    public async Task<bool> AddAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        await RemoveMemberAsync(groupId, userId, cancellationToken);
        return await _associationService.AddAssociationAsync(userId, GraphAssociationType.Admin, groupId, cancellationToken);
    }

    public Task<bool> RemoveAdminAsync(long groupId, long userId, CancellationToken cancellationToken = default)
    {
        return _associationService.DeleteOneAssociationAsync(userId, GraphAssociationType.Admin, groupId, cancellationToken);
    }

    private async Task AddOwnedPhotoAsync(long ownerId, string url, CancellationToken cancellationToken)
    {
        var media = await _objectService.AddObjectAsync(
            GraphObjectType.Media,
            GraphJson.MediaJson(GraphMediaType.Photo, url),
            cancellationToken);

        await _associationService.AddAssociationAsync(ownerId, GraphAssociationType.Owned, media.id, cancellationToken);
    }

    private async Task<bool> CanViewGroupAsync(
        long userId,
        SocialGraphObjectResult group,
        CancellationToken cancellationToken)
    {
        var data = GraphJson.ParseObject(group.data);
        if (GraphJson.Int(data, "privacy") == 0)
        {
            return true;
        }

        return await _dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(
                item => item.id1 == userId &&
                    item.id2 == group.id &&
                    (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin),
                cancellationToken);
    }
}
