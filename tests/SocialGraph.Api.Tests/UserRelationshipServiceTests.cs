namespace SocialGraph.Api.Tests;

using Moq;
using SocialGraph.Api.Service;

public sealed class UserRelationshipServiceTests
{
    private const long UserA = 9_000_000_000_000_001;
    private const long UserB = 9_000_000_000_000_002;

    [Fact]
    public async Task SendFriendRequest_PersistsRequestEdgeBeforeNotification()
    {
        var objects = UsersExist();
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.AddAssociationAsync(UserA, GraphAssociationType.FriendRequest, UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new UserGraphService(objects.Object, associations.Object, external.Object);

        var result = await service.SendFriendRequestAsync(UserA, UserB);

        Assert.True(result);
        associations.Verify(
            item => item.AddAssociationAsync(UserA, GraphAssociationType.FriendRequest, UserB, It.IsAny<CancellationToken>()),
            Times.Once);
        external.Verify(
            item => item.NotifyAsync(UserA, UserB, ExternalNotificationAction.FriendRequest, UserA, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendFriendRequest_IsRejectedWhenEitherUserBlockedTheOther()
    {
        var objects = UsersExist();
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.HasAssociationAsync(UserA, GraphAssociationType.BlockedBy, UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new UserGraphService(objects.Object, associations.Object, external.Object);

        var result = await service.SendFriendRequestAsync(UserA, UserB);

        Assert.False(result);
        associations.Verify(
            item => item.AddAssociationAsync(It.IsAny<long>(), It.IsAny<short>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
        external.Verify(
            item => item.NotifyAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<short>(), It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptFriendRequest_RemovesPendingAndFollowEdgesThenCreatesFriendship()
    {
        var objects = UsersExist();
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.HasAssociationAsync(UserA, GraphAssociationType.FriendRequest, UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        IReadOnlyCollection<AssociationMutation>? applied = null;
        associations
            .Setup(item => item.ApplyMutationsAsync(It.IsAny<IReadOnlyCollection<AssociationMutation>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<AssociationMutation>, CancellationToken>((items, _) => applied = items)
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new UserGraphService(objects.Object, associations.Object, external.Object);

        var result = await service.AcceptFriendRequestAsync(UserA, UserB);

        Assert.True(result);
        Assert.NotNull(applied);
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.FriendRequest, UserB, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.Followed, UserB, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserB, GraphAssociationType.Followed, UserA, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.Friend, UserB, true));
        external.Verify(item => item.NotifyAsync(UserB, UserA, ExternalNotificationAction.FriendAccept, UserB, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Block_RemovesLowerPriorityRelationshipsAndPendingRequests()
    {
        var objects = UsersExist();
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        IReadOnlyCollection<AssociationMutation>? applied = null;
        associations
            .Setup(item => item.ApplyMutationsAsync(It.IsAny<IReadOnlyCollection<AssociationMutation>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<AssociationMutation>, CancellationToken>((items, _) => applied = items)
            .ReturnsAsync(true);
        var service = new UserGraphService(
            objects.Object,
            associations.Object,
            Mock.Of<IExternalServiceClient>());

        var result = await service.BlockUserAsync(UserA, UserB);

        Assert.True(result);
        Assert.NotNull(applied);
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.Friend, UserB, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.FriendRequest, UserB, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserB, GraphAssociationType.FriendRequest, UserA, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.Followed, UserB, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserB, GraphAssociationType.Followed, UserA, false));
        Assert.Contains(applied, item => item == new AssociationMutation(UserA, GraphAssociationType.Blocked, UserB, true));
    }

    private static Mock<IObjectService> UsersExist()
    {
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects
            .Setup(item => item.RetrieveObjectAsync(UserA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserA, GraphObjectType.User, "{}"));
        objects
            .Setup(item => item.RetrieveObjectAsync(UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserB, GraphObjectType.User, "{}"));
        return objects;
    }
}
