namespace SocialGraph.Api.Tests;

using HotChocolate;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class GroupInviteCandidateTests
{
    private const long ViewerId = 100;
    private const long GroupId = 500;

    [Fact]
    public async Task Candidates_ReturnOnlyUnblockedFriendsWithoutMembershipOrPendingRequest()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Object(GroupId, GraphObjectType.Group),
            User(201), User(202), User(203), User(204), User(205), User(206));
        context.AssociationsTb.AddRange(
            Edge(ViewerId, GraphAssociationType.Member, GroupId, 10),
            Edge(ViewerId, GraphAssociationType.Friend, 201, 6_000),
            Edge(ViewerId, GraphAssociationType.Friend, 202, 5_000),
            Edge(ViewerId, GraphAssociationType.Friend, 203, 4_000),
            Edge(ViewerId, GraphAssociationType.Friend, 204, 3_000),
            Edge(ViewerId, GraphAssociationType.Friend, 205, 2_000),
            Edge(ViewerId, GraphAssociationType.Friend, 206, 1_000),
            Edge(202, GraphAssociationType.Member, GroupId, 20),
            Edge(GroupId, GraphAssociationType.HaveAdmin, 203, 21),
            Edge(204, GraphAssociationType.GroupJoinRequest, GroupId, 22),
            Edge(GroupId, GraphAssociationType.HaveGroupJoinRequest, 205, 23),
            Edge(ViewerId, GraphAssociationType.BlockedBy, 206, 24));
        await context.SaveChangesAsync();

        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfilesForViewerAsync(
                ViewerId,
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 201 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Profile(201, "Eligible", isVerified: true) });
        var service = CreateService(context, users.Object);

        var page = await service.GetGroupInviteCandidatesAsync(ViewerId, GroupId, 50, null);

        var candidate = Assert.Single(page.Items);
        Assert.Equal(201, candidate.Id);
        Assert.Equal("Eligible", candidate.Name);
        Assert.True(candidate.IsVerified);
        Assert.False(page.HasNextPage);
        Assert.False(string.IsNullOrWhiteSpace(page.EndCursor));
        users.VerifyAll();
    }

    [Fact]
    public async Task Candidates_UseBoundedKeysetPaginationAndPreserveFriendOrder()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(
            Object(GroupId, GraphObjectType.Group),
            User(301), User(302), User(303));
        context.AssociationsTb.AddRange(
            Edge(ViewerId, GraphAssociationType.Admin, GroupId, 10),
            Edge(ViewerId, GraphAssociationType.Friend, 301, 3_000),
            Edge(ViewerId, GraphAssociationType.Friend, 302, 2_000),
            Edge(ViewerId, GraphAssociationType.Friend, 303, 1_000));
        await context.SaveChangesAsync();

        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        users.Setup(item => item.GetProfilesForViewerAsync(
                ViewerId,
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 301, 302 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Profile(302, "Second"), Profile(301, "First") });
        users.Setup(item => item.GetProfilesForViewerAsync(
                ViewerId,
                It.Is<IReadOnlyCollection<long>>(ids => ids.SequenceEqual(new long[] { 303 })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Profile(303, "Third") });
        var service = CreateService(context, users.Object);

        var first = await service.GetGroupInviteCandidatesAsync(ViewerId, GroupId, 2, null);
        var second = await service.GetGroupInviteCandidatesAsync(ViewerId, GroupId, 2, first.EndCursor);

        Assert.Equal(new long[] { 301, 302 }, first.Items.Select(item => item.Id));
        Assert.True(first.HasNextPage);
        Assert.Equal(new long[] { 303 }, second.Items.Select(item => item.Id));
        Assert.False(second.HasNextPage);
        users.VerifyAll();
    }

    [Fact]
    public async Task Candidates_RejectNonParticipantBeforeHydratingFriends()
    {
        await using var context = CreateContext();
        context.ObjectsTb.AddRange(Object(GroupId, GraphObjectType.Group), User(201));
        context.AssociationsTb.Add(Edge(ViewerId, GraphAssociationType.Friend, 201, 1_000));
        await context.SaveChangesAsync();
        var users = new Mock<IUserGraphService>(MockBehavior.Strict);
        var service = CreateService(context, users.Object);

        var page = await service.GetGroupInviteCandidatesAsync(ViewerId, GroupId, 50, null);

        Assert.Empty(page.Items);
        Assert.False(page.HasNextPage);
        users.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(GraphAssociationType.Member, false)]
    [InlineData(GraphAssociationType.GroupJoinRequest, false)]
    [InlineData(GraphAssociationType.HaveMember, true)]
    [InlineData(GraphAssociationType.HaveGroupJoinRequest, true)]
    public async Task InviteMutation_RejectsExistingOrPendingTargetFromEitherEdgeDirection(
        short associationType,
        bool inverse)
    {
        const long targetId = 700;
        await using var context = CreateContext();
        context.AssociationsTb.Add(inverse
            ? Edge(GroupId, associationType, targetId, 20)
            : Edge(targetId, associationType, GroupId, 20));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        objects.Setup(item => item.RetrieveObjectAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(targetId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(GroupId, GraphObjectType.Group, "{}"));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Member,
                GroupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = CreateService(context, Mock.Of<IUserGraphService>(), objects.Object, associations.Object, external.Object);

        Assert.False(await service.InviteUserAsync(ViewerId, GroupId, targetId));
        external.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(GraphAssociationType.Blocked)]
    [InlineData(GraphAssociationType.BlockedBy)]
    public async Task InviteMutation_RejectsBlockInEitherDirectionWithoutNotification(short blockType)
    {
        const long targetId = 701;
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        objects.Setup(item => item.RetrieveObjectAsync(targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(targetId, GraphObjectType.User, "{}"));
        objects.Setup(item => item.RetrieveObjectAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(GroupId, GraphObjectType.Group, "{}"));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Member,
                GroupId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                GraphAssociationType.Friend,
                targetId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(
                ViewerId,
                blockType,
                targetId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = CreateService(
            context,
            Mock.Of<IUserGraphService>(),
            objects.Object,
            associations.Object,
            external.Object);

        Assert.False(await service.InviteUserAsync(ViewerId, GroupId, targetId));

        objects.VerifyAll();
        external.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Query_DerivesViewerFromTrustedContext()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        groups.Setup(item => item.GetGroupInviteCandidatesAsync(
                ViewerId,
                GroupId,
                25,
                "cursor",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserSummaryPageResult(Array.Empty<UserSummaryResult>(), null, false));
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Returns(ViewerId);

        var result = await new Query().GetGroupInviteCandidatesAsync(
            GroupId,
            25,
            "cursor",
            groups.Object,
            trusted.Object,
            CancellationToken.None);

        Assert.Empty(result.Items);
        groups.VerifyAll();
        trusted.VerifyAll();
    }

    [Fact]
    public async Task Query_RejectsUntrustedCallerBeforeReadingCandidates()
    {
        var groups = new Mock<IGroupGraphService>(MockBehavior.Strict);
        var trusted = new Mock<ITrustedCallerAccessor>(MockBehavior.Strict);
        trusted.Setup(item => item.RequireUserId()).Throws(new GraphQLException("untrusted"));

        await Assert.ThrowsAsync<GraphQLException>(() => new Query().GetGroupInviteCandidatesAsync(
            GroupId,
            25,
            null,
            groups.Object,
            trusted.Object,
            CancellationToken.None));

        groups.VerifyNoOtherCalls();
        trusted.VerifyAll();
    }

    private static GroupGraphService CreateService(
        MyDbContext context,
        IUserGraphService users,
        IObjectService? objects = null,
        IAssociationService? associations = null,
        IExternalServiceClient? external = null) => new(
        context,
        objects ?? Mock.Of<IObjectService>(),
        associations ?? Mock.Of<IAssociationService>(),
        external ?? Mock.Of<IExternalServiceClient>(),
        new BlockVisibilityService(context),
        users,
        TimeProvider.System);

    private static Objects User(long id) => Object(id, GraphObjectType.User);

    private static Objects Object(long id, short type) => new()
    {
        id = id,
        otype = type,
        data = "{}"
    };

    private static UserProfileResult Profile(long id, string name, bool isVerified = false) => new(
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
        isVerified,
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
