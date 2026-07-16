namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class GroupShortcutServiceTests
{
    private const long UserId = 100;

    [Fact]
    public async Task VisitedGroups_UsesStableKeysetCursorAcrossPages()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Group(301, "Newest"),
            Group(302, "Same timestamp, larger id"),
            Group(303, "Same timestamp, smaller id"),
            Group(304, "Oldest"));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Visited, 301, 3_000),
            Edge(UserId, GraphAssociationType.Visited, 302, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 303, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 304, 1_000));
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.GetVisitedGroupsAsync(UserId, 2, null);
        var second = await service.GetVisitedGroupsAsync(UserId, 2, first.EndCursor);

        Assert.Equal(new long[] { 301, 303 }, first.Items.Select(item => item.Id));
        Assert.True(first.HasNextPage);
        Assert.False(string.IsNullOrWhiteSpace(first.EndCursor));
        Assert.Equal(new long[] { 302, 304 }, second.Items.Select(item => item.Id));
        Assert.False(second.HasNextPage);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
    }

    [Fact]
    public async Task VisitedGroups_HidesPrivateGroupUnlessViewerIsMember()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Group(310, "Public", privacy: 0),
            Group(311, "Private hidden", privacy: 1),
            Group(312, "Private member", privacy: 1));
        context.AssociationsTb.AddRange(
            Edge(UserId, GraphAssociationType.Visited, 310, 3_000),
            Edge(UserId, GraphAssociationType.Visited, 311, 2_000),
            Edge(UserId, GraphAssociationType.Visited, 312, 1_000),
            Edge(UserId, GraphAssociationType.Member, 312, 4_000));
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var page = await service.GetVisitedGroupsAsync(UserId, 10, null);

        Assert.Equal(new long[] { 310, 312 }, page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task RecordGroupVisit_UpsertsVisitedAssociationForVisibleGroup()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects
            .Setup(item => item.RetrieveObjectAsync(320, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(320, GraphObjectType.Group, GroupJson("Public", 0)));
        var associations = new Mock<IAssociationService>();
        associations
            .Setup(item => item.AddAssociationAsync(
                UserId,
                GraphAssociationType.Visited,
                320,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = CreateService(context, objects, associations);

        var recorded = await service.RecordGroupVisitAsync(UserId, 320);

        Assert.True(recorded);
        associations.VerifyAll();
    }

    [Fact]
    public async Task PrivateGroupJoin_CreatesPendingEdgeAndNotifiesAdministrators()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(330, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(330, GraphObjectType.Group, GroupJson("Private", 1)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 330, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(330, GraphAssociationType.HaveAdmin, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(200, 1) }, null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new GroupGraphService(context, objects.Object, associations.Object, external.Object);

        var result = await service.RequestJoinAsync(UserId, 330);

        Assert.True(result);
        associations.Verify(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 330, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.NotifyAsync(UserId, 200, ExternalNotificationAction.GroupJoin, 330, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublicGroupJoin_AddsMemberWithoutPendingApproval()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>();
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(331, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(331, GraphObjectType.Group, GroupJson("Public", 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.ApplyMutationsAsync(It.IsAny<IReadOnlyCollection<AssociationMutation>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new GroupGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        var result = await service.RequestJoinAsync(UserId, 331);

        Assert.True(result);
        associations.Verify(item => item.ApplyMutationsAsync(
            It.Is<IReadOnlyCollection<AssociationMutation>>(items =>
                items.Contains(new AssociationMutation(UserId, GraphAssociationType.Member, 331, true))),
            It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.AddAssociationAsync(UserId, GraphAssociationType.GroupJoinRequest, 331, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GroupInvite_RequiresAdminAndQueuesCanonicalNotification()
    {
        await using var context = CreateContext();
        const long adminId = 200;
        const long groupId = 340;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, GroupJson("Invite", 1)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(adminId, GraphAssociationType.Admin, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new GroupGraphService(context, objects.Object, associations.Object, external.Object);

        var invited = await service.InviteUserAsync(adminId, groupId, UserId);

        Assert.True(invited);
        external.Verify(item => item.NotifyAsync(
            adminId,
            UserId,
            ExternalNotificationAction.GroupInvite,
            groupId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);

        associations.Setup(item => item.HasAssociationAsync(201, GraphAssociationType.Admin, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        Assert.False(await service.InviteUserAsync(201, groupId, UserId));
    }

    private static GroupGraphService CreateService(
        MyDbContext context,
        Mock<IObjectService>? objects = null,
        Mock<IAssociationService>? associations = null) => new(
            context,
            (objects ?? new Mock<IObjectService>()).Object,
            (associations ?? new Mock<IAssociationService>()).Object,
            Mock.Of<IExternalServiceClient>());

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static Objects Group(long id, string name, int privacy = 0) => new()
    {
        id = id,
        otype = GraphObjectType.Group,
        data = GroupJson(name, privacy)
    };

    private static string GroupJson(string name, int privacy) => new JsonObject
    {
        ["name"] = name,
        ["avatar"] = $"https://cdn.example/{name}.jpg",
        ["privacy"] = privacy
    }.ToJsonString();

    private static Associations Edge(long id1, short type, long id2, long time) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = time
    };
}
