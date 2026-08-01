namespace SocialGraph.Api.Tests;

using HotChocolate;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class GroupFriendMemberPreviewTests
{
    private const long ViewerId = 100;
    private const long GroupId = 500;

    [Fact]
    public async Task Preview_ReturnsOnlyCurrentUnblockedFriendsWhoParticipateInTheTargetGroup()
    {
        await using var context = CreateContext();
        context.ObjectsTb.Add(new Objects { id = GroupId, otype = GraphObjectType.Group, data = "{}" });
        context.AssociationsTb.AddRange(
            Edge(ViewerId, GraphAssociationType.Friend, 201, 3_000),
            Edge(ViewerId, GraphAssociationType.Friend, 202, 2_000),
            Edge(ViewerId, GraphAssociationType.Friend, 203, 1_000),
            Edge(ViewerId, GraphAssociationType.Friend, 204, 500),
            Edge(201, GraphAssociationType.Member, GroupId, 10),
            Edge(202, GraphAssociationType.Admin, GroupId, 11),
            Edge(203, GraphAssociationType.Member, GroupId, 12),
            Edge(ViewerId, GraphAssociationType.Blocked, 203, 13));
        await context.SaveChangesAsync();

        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfilesForViewerAsync(
                ViewerId,
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 201, 202 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Profile(202, "Second"), Profile(201, "First") });
        var service = CreateService(context, users.Object);

        var result = await service.GetGroupFriendMembersAsync(ViewerId, GroupId, 12);

        Assert.Equal(new long[] { 201, 202 }, result.Select(item => item.Id));
        Assert.All(result, item => Assert.False(string.IsNullOrWhiteSpace(item.Name)));
        users.VerifyAll();
    }

    [Fact]
    public async Task Query_DerivesViewerFromTrustedContext()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.GetGroupFriendMembersAsync(
                ViewerId,
                GroupId,
                12,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GroupSuggestionFriendResult>());
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetGroupFriendMembersAsync(
            GroupId,
            12,
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result);
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task Query_RejectsUntrustedCallerBeforeReadingMemberships()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Throws(new GraphQLException("untrusted"));

        await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetGroupFriendMembersAsync(
            GroupId,
            12,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.VerifyNoOtherCalls();
        trusted.VerifyAll();
    }

    private static GroupGraphService CreateService(MyDbContext context, IUserGraphService users) => new(
        context,
        Mock.Of<IObjectService>(),
        Mock.Of<IAssociationService>(),
        Mock.Of<IExternalServiceClient>(),
        new BlockVisibilityService(context),
        users,
        TimeProvider.System);

    private static UserProfileResult Profile(long id, string name) => new(
        id,
        $"https://cdn.example/{id}.jpg",
        string.Empty,
        name,
        string.Empty,
        1,
        "2000-01-01",
        string.Empty,
        0,
        string.Empty,
        null,
        false,
        0,
        0,
        0);

    private static Associations Edge(long id1, short type, long id2, long time) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = time
    };

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }
}
