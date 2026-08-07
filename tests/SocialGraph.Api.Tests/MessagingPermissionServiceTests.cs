namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class MessagingPermissionServiceTests
{
    [Theory]
    [InlineData("CREATE_DIRECT")]
    [InlineData("SEND_DIRECT")]
    public async Task DirectMessaging_AllowsExistingNonFriendAndDeniesEitherBlockDirection(string action)
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(User(1), User(2), User(3), User(4));
        context.AssociationsTb.AddRange(
            Edge(1, GraphAssociationType.Blocked, 3),
            Edge(1, GraphAssociationType.BlockedBy, 4));
        await context.SaveChangesAsync();
        var service = new MessagingPermissionService(context);

        var result = await service.CheckAsync(new MessagingPermissionCheckRequest(
            1,
            new long[] { 2, 3, 4 },
            action));

        var nonFriend = result.Results.Single(item => item.TargetUserId == 2);
        Assert.True(nonFriend.Allowed);
        Assert.False(nonFriend.IsFriend);
        Assert.False(nonFriend.BlockedEitherDirection);
        Assert.Null(nonFriend.Reason);
        Assert.All(
            result.Results.Where(item => item.TargetUserId is 3 or 4),
            blocked =>
            {
                Assert.False(blocked.Allowed);
                Assert.True(blocked.BlockedEitherDirection);
                Assert.Equal("BLOCKED", blocked.Reason);
            });
    }

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

    [Theory]
    [InlineData("ADD_GROUP_MEMBERS")]
    [InlineData("VIEW_PRESENCE")]
    public async Task FriendOnlyActions_ContinueToDenyUnblockedNonFriends(string action)
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(User(1), User(2));
        await context.SaveChangesAsync();
        var service = new MessagingPermissionService(context);

        var result = await service.CheckAsync(new MessagingPermissionCheckRequest(
            1,
            new long[] { 2 },
            action));

        var decision = Assert.Single(result.Results);
        Assert.False(decision.Allowed);
        Assert.False(decision.IsFriend);
        Assert.False(decision.BlockedEitherDirection);
        Assert.Equal("NOT_FRIENDS", decision.Reason);
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

    [Fact]
    public async Task InspectBlock_ReturnsDirectionalStateWithoutGrantingMessagingPermission()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(User(1), User(2), User(3));
        context.AssociationsTb.AddRange(
            Edge(1, GraphAssociationType.Blocked, 2),
            Edge(1, GraphAssociationType.BlockedBy, 3));
        await context.SaveChangesAsync();
        var service = new MessagingPermissionService(context);

        var result = await service.CheckAsync(new MessagingPermissionCheckRequest(
            1,
            new long[] { 2, 3 },
            "INSPECT_BLOCK"));

        var actorBlocked = result.Results.Single(item => item.TargetUserId == 2);
        Assert.True(actorBlocked.Allowed);
        Assert.True(actorBlocked.ActorBlockedTarget);
        Assert.False(actorBlocked.TargetBlockedActor);
        var targetBlocked = result.Results.Single(item => item.TargetUserId == 3);
        Assert.True(targetBlocked.Allowed);
        Assert.False(targetBlocked.ActorBlockedTarget);
        Assert.True(targetBlocked.TargetBlockedActor);
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

