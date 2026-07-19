namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;
using System.Text.Json;

public sealed class UserGraphServiceTests
{
    private const long UserId = 9_000_000_000_000_001;

    [Fact]
    public async Task CreateUser_UsesSocialGraphIdForExternalProvisioning()
    {
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .Setup(service => service.AddObjectAsync(GraphObjectType.User, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.CreateUserAsync(
                UserId,
                "a@example.com",
                "secret",
                "Nguyen Van A",
                "2000-01-01",
                true,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object);

        var result = await service.CreateUserAsync(Input());

        Assert.True(result.Success);
        Assert.Equal(UserId, result.UserId);
        externalService.VerifyAll();
        objectService.VerifyAll();
    }

    [Fact]
    public async Task CreateUser_RollsBackSocialGraphObject_WhenAuthenticationProvisioningFails()
    {
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .Setup(service => service.AddObjectAsync(GraphObjectType.User, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objectService
            .Setup(service => service.DeleteObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.CreateUserAsync(
                UserId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceCallException("AuthenticationServiceCreateUser", "HTTP 409"));
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object);

        var result = await service.CreateUserAsync(Input());

        Assert.False(result.Success);
        Assert.Null(result.UserId);
        objectService.Verify(service => service.DeleteObjectAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProfilesForViewer_BatchesProfilesCountsAndBlockFiltering()
    {
        const long viewerId = 101;
        const long visibleUserId = 102;
        const long blockedUserId = 103;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(visibleUserId, "Visible Friend"),
            User(blockedUserId, "Blocked Friend"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Blocked, blockedUserId),
            Edge(visibleUserId, GraphAssociationType.Friend, viewerId),
            Edge(visibleUserId, GraphAssociationType.Friend, 104),
            Edge(visibleUserId, GraphAssociationType.FollowedBy, 105),
            Edge(visibleUserId, GraphAssociationType.Followed, 106));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var profiles = await service.GetProfilesForViewerAsync(
            viewerId,
            new[] { visibleUserId, blockedUserId });

        var profile = Assert.Single(profiles);
        Assert.Equal(visibleUserId, profile.Id);
        Assert.Equal("Visible Friend", profile.Name);
        Assert.Equal(2, profile.FriendCount);
        Assert.Equal(1, profile.FollowerCount);
        Assert.Equal(1, profile.FollowingCount);
    }

    [Fact]
    public async Task GetFriendRelationProfiles_LoadsEveryFriendsPageAndIncludesOwnBlockList()
    {
        const long viewerId = 151;
        const long friendId = 152;
        const long outgoingId = 153;
        const long incomingId = 154;
        const long blockedId = 155;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(friendId, "Friend"),
            User(outgoingId, "Outgoing"),
            User(incomingId, "Incoming"),
            User(blockedId, "Blocked"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Friend, friendId),
            Edge(viewerId, GraphAssociationType.FriendRequest, outgoingId),
            Edge(viewerId, GraphAssociationType.HaveFriendRequest, incomingId),
            Edge(viewerId, GraphAssociationType.Blocked, blockedId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var friends = await service.GetFriendRelationProfilesAsync(viewerId, GraphAssociationType.Friend, 100);
        var outgoing = await service.GetFriendRelationProfilesAsync(viewerId, GraphAssociationType.FriendRequest, 100);
        var incoming = await service.GetFriendRelationProfilesAsync(viewerId, GraphAssociationType.HaveFriendRequest, 100);
        var blocked = await service.GetFriendRelationProfilesAsync(viewerId, GraphAssociationType.Blocked, 100);

        Assert.Equal(friendId, Assert.Single(friends).Id);
        Assert.Equal(outgoingId, Assert.Single(outgoing).Id);
        Assert.Equal(incomingId, Assert.Single(incoming).Id);
        Assert.Equal(blockedId, Assert.Single(blocked).Id);
    }

    [Fact]
    public async Task GetFriendIds_ReturnsOnlyExistingUnblockedAcceptedFriends()
    {
        const long viewerId = 171;
        const long friendId = 172;
        const long blockedFriendId = 173;
        const long pendingId = 174;
        const long deletedFriendId = 175;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(friendId, "Friend"),
            User(blockedFriendId, "Blocked Friend"),
            User(pendingId, "Pending"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Friend, friendId),
            Edge(viewerId, GraphAssociationType.Friend, blockedFriendId),
            Edge(viewerId, GraphAssociationType.Blocked, blockedFriendId),
            Edge(viewerId, GraphAssociationType.FriendRequest, pendingId),
            Edge(viewerId, GraphAssociationType.Friend, deletedFriendId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var ids = await service.GetFriendIdsAsync(viewerId);

        Assert.Equal(new[] { friendId }, ids);
    }

    [Fact]
    public async Task GetFriendSuggestions_RanksMutualFriendsAndExcludesExistingRelationships()
    {
        const long viewerId = 201;
        const long friendId = 202;
        const long mutualCandidateId = 203;
        const long pendingId = 204;
        const long blockedId = 205;
        const long fallbackCandidateId = 206;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(friendId, "Mutual Friend"),
            User(mutualCandidateId, "Mutual Candidate"),
            User(pendingId, "Pending Candidate"),
            User(blockedId, "Blocked Candidate"),
            User(fallbackCandidateId, "Fallback Candidate"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Friend, friendId),
            Edge(friendId, GraphAssociationType.Friend, viewerId),
            Edge(friendId, GraphAssociationType.Friend, mutualCandidateId),
            Edge(mutualCandidateId, GraphAssociationType.Friend, friendId),
            Edge(viewerId, GraphAssociationType.FriendRequest, pendingId),
            Edge(viewerId, GraphAssociationType.Blocked, blockedId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var suggestions = await service.GetFriendSuggestionsAsync(viewerId, 10);

        Assert.Equal(new[] { mutualCandidateId, fallbackCandidateId }, suggestions.Select(item => item.Profile.Id));
        Assert.Equal(1, suggestions[0].MutualFriendCount);
        Assert.Equal(friendId, Assert.Single(suggestions[0].MutualFriends).Id);
        Assert.DoesNotContain(suggestions, item => item.Profile.Id == pendingId || item.Profile.Id == blockedId);
    }

    private static CreateUserInput Input()
    {
        return new CreateUserInput(
            "Nguyen Van A",
            true,
            "2000-01-01",
            "Ha Noi",
            "a@example.com",
            "secret");
    }

    private static Objects User(long id, string name) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = JsonSerializer.Serialize(new
        {
            avatar = "",
            background = "",
            name,
            bio = "",
            gender = 1,
            birthdate = "2000-01-01",
            location = "Ha Noi",
            verify = (string?)null,
            privacy = 0,
            create = "2026-01-01T00:00:00Z"
        })
    };

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}
