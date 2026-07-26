namespace SocialGraph.Api.Tests;

using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

/// <summary>
/// Regression coverage for the media takeover chain.
/// changeUserAvatar only checked that the caller owned the *profile* being edited, never that the
/// caller owned the *media URL* being stored. An attacker could therefore store a victim's avatar
/// URL on their own profile and then replace it, causing the victim's file to be permanently
/// deleted as the "previous" avatar.
/// </summary>
public sealed class MediaOwnershipTests
{
    private const long Attacker = 9_000_000_000_000_002;
    private const string VictimMediaUrl = "/media/files/0123456789abcdef0123456789abcdef.png";

    [Fact]
    public async Task ChangeUserAvatar_rejects_media_the_caller_does_not_own()
    {
        var objectService = BuildObjectService();
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            dbContext: null,
            contentGraphService: null,
            mediaOwnershipGuard: RejectingGuard());

        await Assert.ThrowsAsync<MediaOwnershipException>(
            () => service.ChangeUserAvatarAsync(Attacker, VictimMediaUrl));

        // The write must not happen, and above all no deletion may be queued.
        objectService.Verify(
            item => item.UpdateObjectAsync(
                It.IsAny<long>(),
                It.IsAny<short>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        externalService.Verify(
            item => item.DeleteMediaAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangeUserBackground_rejects_media_the_caller_does_not_own()
    {
        var objectService = BuildObjectService();
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            dbContext: null,
            contentGraphService: null,
            mediaOwnershipGuard: RejectingGuard());

        await Assert.ThrowsAsync<MediaOwnershipException>(
            () => service.ChangeUserBackgroundAsync(Attacker, VictimMediaUrl));

        externalService.Verify(
            item => item.DeleteMediaAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangeUserAvatar_scopes_media_lifecycle_calls_to_the_owner()
    {
        var objectService = BuildObjectService();
        objectService
            .Setup(item => item.UpdateObjectAsync(
                Attacker,
                GraphObjectType.User,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(Attacker, GraphObjectType.User, "{}"));

        var externalService = new Mock<IExternalServiceClient>();
        var guard = new Mock<IMediaOwnershipGuard>();
        guard
            .Setup(item => item.EnsureOwnedAsync(
                It.IsAny<long>(),
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            dbContext: null,
            contentGraphService: null,
            mediaOwnershipGuard: guard.Object);

        await service.ChangeUserAvatarAsync(Attacker, "/media/files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png");

        guard.Verify(
            item => item.EnsureOwnedAsync(Attacker, It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Upload must be told whose asset this is so it can refuse foreign files.
        externalService.Verify(
            item => item.FinalizeMediaAsync(
                It.IsAny<IReadOnlyList<string>>(),
                Attacker,
                It.IsAny<CancellationToken>()),
            Times.Once);
        externalService.Verify(
            item => item.DeleteMediaAsync(
                It.Is<IReadOnlyList<string>>(urls => urls.Contains(VictimMediaUrl)),
                Attacker,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<IObjectService> BuildObjectService()
    {
        var objectService = new Mock<IObjectService>();
        objectService
            .Setup(item => item.RetrieveObjectAsync(Attacker, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(
                Attacker,
                GraphObjectType.User,
                $"{{\"avatar\":\"{VictimMediaUrl}\",\"background\":\"{VictimMediaUrl}\"}}"));
        return objectService;
    }

    private static IMediaOwnershipGuard RejectingGuard()
    {
        var guard = new Mock<IMediaOwnershipGuard>();
        guard
            .Setup(item => item.EnsureOwnedAsync(
                It.IsAny<long>(),
                It.IsAny<IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MediaOwnershipException([VictimMediaUrl]));
        return guard.Object;
    }
}
