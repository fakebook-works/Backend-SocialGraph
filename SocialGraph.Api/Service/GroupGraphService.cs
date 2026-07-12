namespace SocialGraph.Api.Service;

using System.Text;
using System.Text.Json;
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
        var take = Math.Clamp(limit, 1, 100);
        var query = _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId && item.atype == GraphAssociationType.Visited);
        if (TryDecodeVisitedGroupCursor(cursor, out var decodedCursor))
        {
            query = query.Where(item => item.time < decodedCursor.VisitTime ||
                item.time == decodedCursor.VisitTime && item.id2 < decodedCursor.GroupId);
        }

        var pageEdges = await query
            .OrderByDescending(item => item.time)
            .ThenByDescending(item => item.id2)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var selectedEdges = pageEdges.Take(take).ToArray();
        if (selectedEdges.Length == 0)
        {
            return new VisitedGroupPageResult(Array.Empty<VisitedGroupResult>(), null, false);
        }

        var groupIds = selectedEdges.Select(item => item.id2).Distinct().ToArray();
        var groups = await _dbContext.ObjectsTb
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.id) && item.otype == GraphObjectType.Group)
            .ToDictionaryAsync(item => item.id, cancellationToken);
        var participatingGroupIds = (await _dbContext.AssociationsTb
            .AsNoTracking()
            .Where(item => item.id1 == userId &&
                groupIds.Contains(item.id2) &&
                (item.atype == GraphAssociationType.Member || item.atype == GraphAssociationType.Admin))
            .Select(item => item.id2)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();
        var items = new List<VisitedGroupResult>(selectedEdges.Length);

        foreach (var edge in selectedEdges)
        {
            if (!groups.TryGetValue(edge.id2, out var group))
            {
                continue;
            }

            var data = GraphJson.ParseObject(group.data);
            if (GraphJson.Int(data, "privacy") != 0 && !participatingGroupIds.Contains(group.id))
            {
                continue;
            }

            items.Add(new VisitedGroupResult(
                group.id,
                GraphJson.String(data, "avatar"),
                GraphJson.String(data, "name")));
        }

        var lastScannedEdge = selectedEdges[^1];
        return new VisitedGroupPageResult(
            items,
            EncodeVisitedGroupCursor(lastScannedEdge.time, lastScannedEdge.id2),
            pageEdges.Count > take);
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

    private static string EncodeVisitedGroupCursor(long visitTime, long groupId)
    {
        var payload = JsonSerializer.Serialize(new VisitedGroupCursor(visitTime, groupId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static bool TryDecodeVisitedGroupCursor(string? cursor, out VisitedGroupCursor decodedCursor)
    {
        decodedCursor = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parsed = JsonSerializer.Deserialize<VisitedGroupCursor>(json);
            if (parsed.VisitTime <= 0 || parsed.GroupId <= 0)
            {
                return false;
            }

            decodedCursor = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct VisitedGroupCursor(long VisitTime, long GroupId);
}
