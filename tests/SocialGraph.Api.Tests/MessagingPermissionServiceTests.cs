namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class MessagingPermissionServiceTests
{
    [Fact]
    public async Task PermissionBatch_AllowsFriendsAndDeniesBlockedOrUnknownTargets()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(User(1), User(2), User(3));
        context.AssociationsTb.AddRange(
            Edge(1, GraphAssociationType.Friend, 2),
            Edge(1, GraphAssociationType.Friend, 3),
            Edge(1, GraphAssociationType.BlockedBy, 3));
        await context.SaveChangesAsync();
        var service = new MessagingPermissionService(context);

        var result = await service.CheckAsync(new MessagingPermissionCheckRequest(
            1,
            new long[] { 2, 3, 4 },
            "ADD_GROUP_MEMBERS"));

        Assert.True(result.Results.Single(item => item.TargetUserId == 2).Allowed);
        var blocked = result.Results.Single(item => item.TargetUserId == 3);
        Assert.False(blocked.Allowed);
        Assert.True(blocked.BlockedEitherDirection);
        Assert.Equal("BLOCKED", blocked.Reason);
        Assert.Equal("USER_NOT_FOUND", result.Results.Single(item => item.TargetUserId == 4).Reason);
    }

    [Fact]
    public async Task PermissionBatch_RejectsDuplicateTargetsAndUnknownActions()
    {
        await using var context = CreateContext();
        var service = new MessagingPermissionService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckAsync(
            new MessagingPermissionCheckRequest(1, new long[] { 2, 2 }, "CREATE_DIRECT")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckAsync(
            new MessagingPermissionCheckRequest(1, new long[] { 2 }, "UNKNOWN")));
    }

    private static Objects User(long id) => new() { id = id, otype = GraphObjectType.User, data = "{}" };

    private static Associations Edge(long id1, short atype, long id2) => new()
    {
        id1 = id1,
        atype = atype,
        id2 = id2,
        time = 1
    };

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }
}

