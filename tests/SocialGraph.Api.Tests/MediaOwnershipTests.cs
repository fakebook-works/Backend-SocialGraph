namespace SocialGraph.Api.Tests;

using Moq;
using Microsoft.EntityFrameworkCore;
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
    private const long GroupId = 9_000_000_000_000_003;
    private const string VictimMediaUrl = "/media/files/0123456789abcdef0123456789abcdef.png";

    [Fact]
    public async Task ChangeUserAvatar_rejects_media_the_caller_does_not_own()
    {
        var objectService = BuildObjectService();
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService.Setup(item => item.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
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
                It.IsAny<IReadOnlyList<MediaLifecycleReference>>(),
                It.IsAny<long?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChangeUserBackground_rejects_media_the_caller_does_not_own()
    {
        var objectService = BuildObjectService();
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService.Setup(item => item.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
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
                It.IsAny<IReadOnlyList<MediaLifecycleReference>>(),
                It.IsAny<long?>(),
                It.IsAny<DateTimeOffset>(),
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
            .Setup(item => item.EnsureReferencesOwnedAsync(
                It.IsAny<long>(),
                It.IsAny<IReadOnlyCollection<MediaLifecycleReference>>(),
                It.IsAny<DateTimeOffset>(),
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
            item => item.EnsureReferencesOwnedAsync(
                Attacker,
                It.IsAny<IReadOnlyCollection<MediaLifecycleReference>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // Upload must be told whose asset this is so it can refuse foreign files.
        externalService.Verify(
            item => item.FinalizeMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == "/media/files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png" &&
                    references[0].ReferenceId == $"socialgraph:user:{Attacker}:avatar"),
                Attacker,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        externalService.Verify(
            item => item.DeleteMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == VictimMediaUrl &&
                    references[0].ReferenceId == $"socialgraph:user:{Attacker}:avatar"),
                null,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ChangeGroupAvatar_AttachesTheActorOwnedCropToTheStableGroupSlot()
    {
        const string croppedUrl = "/media/files/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.png";
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var currentData = GraphJson.GroupJson("Group", null, 0, VictimMediaUrl, null);
        var updatedData = GraphJson.GroupJson("Group", null, 0, croppedUrl, null);
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .SetupSequence(item => item.RetrieveObjectAsync(GroupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(GroupId, GraphObjectType.Group, currentData))
            .ReturnsAsync(new SocialGraphObjectResult(GroupId, GraphObjectType.Group, updatedData));
        objectService
            .Setup(item => item.UpdateObjectAsync(
                GroupId,
                GraphObjectType.Group,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(GroupId, GraphObjectType.Group, updatedData));
        var guard = new Mock<IMediaOwnershipGuard>(MockBehavior.Strict);
        guard.Setup(item => item.EnsureReferencesOwnedAsync(
                Attacker,
                It.Is<IReadOnlyCollection<MediaLifecycleReference>>(references =>
                    references.Any(reference => reference.Url == croppedUrl)),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new GroupGraphService(
            context,
            objectService.Object,
            Mock.Of<IAssociationService>(),
            external.Object,
            Mock.Of<IBlockVisibilityService>(),
            Mock.Of<IUserGraphService>(),
            TimeProvider.System,
            mediaOwnershipGuard: guard.Object);

        var result = await service.ChangeGroupAvatarAsync(Attacker, GroupId, croppedUrl);

        Assert.Equal(croppedUrl, result?.Avatar);
        external.Verify(item => item.FinalizeMediaAsync(
            It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                references.Count == 1 &&
                references[0].Url == croppedUrl &&
                references[0].ReferenceId == $"socialgraph:group:{GroupId}:avatar"),
            Attacker,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.DeleteMediaAsync(
            It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                references.Count == 1 &&
                references[0].Url == VictimMediaUrl &&
                references[0].ReferenceId == $"socialgraph:group:{GroupId}:avatar"),
            null,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
        guard.VerifyAll();
        objectService.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_WhenParentWriteFails_CancelsOnlyTheExactReservation()
    {
        const string nextUrl = "/media/files/cccccccccccccccccccccccccccccccc.avif";
        var operationAt = DateTimeOffset.Parse("2026-08-07T12:34:56Z");
        var objectService = BuildObjectService();
        objectService.Setup(item => item.UpdateObjectAsync(
                Attacker,
                GraphObjectType.User,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated parent write failure"));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        external.Setup(item => item.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(operationAt);
        external.Setup(item => item.FinalizeMediaAsync(
                It.IsAny<IReadOnlyList<MediaLifecycleReference>>(),
                Attacker,
                operationAt,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var guard = new Mock<IMediaOwnershipGuard>(MockBehavior.Strict);
        guard.Setup(item => item.EnsureReferencesOwnedAsync(
                Attacker,
                It.IsAny<IReadOnlyCollection<MediaLifecycleReference>>(),
                operationAt,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        guard.Setup(item => item.CancelReferenceReservationBestEffortAsync(
                Attacker,
                It.Is<IReadOnlyCollection<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references.Single().Url == nextUrl &&
                    references.Single().ReferenceId == $"socialgraph:user:{Attacker}:avatar"),
                operationAt,
                CancellationToken.None))
            .Returns(Task.CompletedTask);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            external.Object,
            mediaOwnershipGuard: guard.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ChangeUserAvatarAsync(Attacker, nextUrl));

        guard.VerifyAll();
        external.VerifyAll();
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
            .Setup(item => item.EnsureReferencesOwnedAsync(
                It.IsAny<long>(),
                It.IsAny<IReadOnlyCollection<MediaLifecycleReference>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MediaOwnershipException([VictimMediaUrl]));
        return guard.Object;
    }
}
