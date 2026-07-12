namespace SocialGraph.Api.Tests;

using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Service;

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
}
