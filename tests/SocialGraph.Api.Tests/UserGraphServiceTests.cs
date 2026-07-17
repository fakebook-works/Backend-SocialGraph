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
