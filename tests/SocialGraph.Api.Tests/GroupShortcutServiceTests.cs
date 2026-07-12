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
