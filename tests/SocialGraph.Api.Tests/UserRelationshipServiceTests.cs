namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Moq;
using SocialGraph.Api.Contracts;
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task FollowUser_RequiresTheTargetToUseAdvancedAccountMode(int targetPrivacy, bool expected)
    {
        var objects = UsersExist(targetPrivacy);
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.AddAssociationAsync(UserA, GraphAssociationType.Followed, UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new UserGraphService(objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        var result = await service.FollowUserAsync(UserA, UserB);

        Assert.Equal(expected, result);
        associations.Verify(
            item => item.AddAssociationAsync(UserA, GraphAssociationType.Followed, UserB, It.IsAny<CancellationToken>()),
            expected ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task UpdateUser_ToNormalMode_RemovesAllIncomingFollowers()
    {
        var updatedObject = new SocialGraphObjectResult(UserB, GraphObjectType.User, UserData(privacy: 0));
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects
            .Setup(item => item.UpdateObjectAsync(
                UserB,
                GraphObjectType.User,
                It.Is<string>(data => JsonNode.Parse(data)!["privacy"]!.GetValue<int>() == 0),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedObject);
        objects
            .Setup(item => item.RetrieveObjectAsync(UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedObject);
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations
            .Setup(item => item.DeleteAllAssociationAsync(UserB, GraphAssociationType.FollowedBy, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new UserGraphService(objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        var result = await service.UpdateUserAsync(new UpdateUserInput(
            UserB, null, null, null, null, null, null, null, Privacy: 0));

        Assert.Equal(0, result?.Privacy);
        associations.Verify(item => item.DeleteAllAssociationAsync(
            UserB,
            GraphAssociationType.FollowedBy,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task UpdateUser_RejectsUnknownAccountPrivacyBeforeWriting(int privacy)
    {
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        var service = new UserGraphService(objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.UpdateUserAsync(new UpdateUserInput(
            UserB, null, null, null, null, null, null, null, privacy)));

        objects.Verify(item => item.UpdateObjectAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IObjectService> UsersExist(int targetPrivacy = 0)
    {
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects
            .Setup(item => item.RetrieveObjectAsync(UserA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserA, GraphObjectType.User, UserData(privacy: 0)));
        objects
            .Setup(item => item.RetrieveObjectAsync(UserB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserB, GraphObjectType.User, UserData(targetPrivacy)));
        return objects;
    }

    private static string UserData(int privacy) => new JsonObject
    {
        ["avatar"] = "",
        ["background"] = "",
        ["name"] = "User",
        ["bio"] = "",
        ["gender"] = 0,
        ["birthdate"] = "",
        ["location"] = "",
        ["privacy"] = privacy,
        ["create"] = "2026-01-01T00:00:00Z"
    }.ToJsonString();
}
