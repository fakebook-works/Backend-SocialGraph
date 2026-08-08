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
    public async Task GetAvatarSource_ReadsSnowflakeStringsAndRejectsMalformedMetadata()
    {
        const long contentId = 9_000_000_000_000_131;
        const long mediaId = 9_000_000_000_000_132;
        var validData = JsonSerializer.Serialize(new
        {
            avatar = "/media/avatar.jpg",
            avatarSource = new { contentId = contentId.ToString(), mediaId = mediaId.ToString() }
        });
        var malformedData = "{\"avatar\":\"/media/avatar.jpg\",\"avatarSource\":{\"contentId\":123,\"mediaId\":\"456\"}}";
        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        objects.SetupSequence(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, validData))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, malformedData));
        var service = new UserGraphService(
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        var source = await service.GetAvatarSourceAsync(UserId);
        var malformed = await service.GetAvatarSourceAsync(UserId);

        Assert.Equal(contentId, source?.ContentId);
        Assert.Equal(mediaId, source?.MediaId);
        Assert.Null(malformed);
    }

    [Fact]
    public async Task ChangeUserAvatar_PublishesTheOriginalUploadAsAPublicAvatarActivity()
    {
        const string croppedUrl = "https://cdn.example/avatar-cropped.jpg";
        const string originalUrl = "https://cdn.example/avatar-original.jpg";
        var current = User(UserId, "Owner");
        var updatedData = JsonSerializer.Serialize(new
        {
            avatar = croppedUrl,
            background = "",
            name = "Owner",
            bio = "",
            gender = 1,
            birthdate = "2000-01-01",
            location = "Ha Noi",
            verify = (string?)null,
            privacy = 0,
            create = "2026-01-01T00:00:00Z"
        });
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .SetupSequence(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, current.data))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        objectService
            .Setup(service => service.UpdateObjectAsync(
                UserId,
                GraphObjectType.User,
                It.Is<string>(json =>
                    json.Contains("\"avatarSource\"", StringComparison.Ordinal) &&
                    json.Contains("\"contentId\":\"100\"", StringComparison.Ordinal) &&
                    json.Contains("\"mediaId\":\"101\"", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
        externalService
            .Setup(service => service.FinalizeMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == croppedUrl &&
                    references[0].ReferenceId == $"socialgraph:user:{UserId}:avatar"),
                UserId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        contentService
            .Setup(service => service.CreateFeedPostAsync(
                It.Is<CreateFeedPostInput>(input =>
                    input.AuthorId == UserId &&
                    input.Content == "đã cập nhật ảnh đại diện" &&
                    input.Privacy == 0 &&
                    input.Media != null &&
                    input.Media.Count == 1 &&
                    input.Media[0].Type == GraphMediaType.Photo &&
                    input.Media[0].Url == originalUrl),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentResult(
                100,
                GraphObjectType.FeedPost,
                "đã cập nhật ảnh đại diện",
                0,
                "2026-01-01T00:00:00Z",
                UserId,
                new[] { new MediaResult(101, GraphMediaType.Photo, originalUrl) }));
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            contentGraphService: contentService.Object);

        var result = await service.ChangeUserAvatarAsync(UserId, croppedUrl, originalUrl, privacy: 3);

        Assert.NotNull(result);
        Assert.Equal(croppedUrl, result.Avatar);
        objectService.VerifyAll();
        externalService.VerifyAll();
        contentService.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_WithAnExistingPhoto_DoesNotPublishAnActivityPost()
    {
        const string croppedUrl = "https://cdn.example/existing-avatar-cropped.jpg";
        var current = User(UserId, "Owner");
        var updatedData = JsonSerializer.Serialize(new
        {
            avatar = croppedUrl,
            background = "",
            name = "Owner",
            bio = "",
            gender = 1,
            birthdate = "2000-01-01",
            location = "Ha Noi",
            verify = (string?)null,
            privacy = 0,
            create = "2026-01-01T00:00:00Z"
        });
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .SetupSequence(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, current.data))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        objectService
            .Setup(service => service.UpdateObjectAsync(
                UserId,
                GraphObjectType.User,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
        externalService
            .Setup(service => service.FinalizeMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == croppedUrl &&
                    references[0].ReferenceId == $"socialgraph:user:{UserId}:avatar"),
                UserId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            contentGraphService: contentService.Object);

        var result = await service.ChangeUserAvatarAsync(UserId, croppedUrl, originalUrl: null, privacy: 3);

        Assert.NotNull(result);
        Assert.Equal(croppedUrl, result.Avatar);
        contentService.Verify(
            candidate => candidate.CreateFeedPostAsync(
                It.IsAny<CreateFeedPostInput>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        objectService.VerifyAll();
        externalService.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_WithExistingSource_ValidatesAndStoresExactPostAndMedia()
    {
        const long contentId = 9_000_000_000_000_101;
        const long mediaId = 9_000_000_000_000_102;
        const string sourceUrl = "https://cdn.example/source.jpg";
        const string croppedUrl = "https://cdn.example/source-cropped.jpg";
        var current = User(UserId, "Owner");
        var updatedData = JsonSerializer.Serialize(new
        {
            avatar = croppedUrl,
            avatarSource = new
            {
                contentId = contentId.ToString(),
                mediaId = mediaId.ToString()
            },
            background = "",
            name = "Owner",
            bio = "",
            gender = 1,
            birthdate = "2000-01-01",
            location = "Ha Noi",
            verify = (string?)null,
            privacy = 0,
            create = "2026-01-01T00:00:00Z"
        });
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .SetupSequence(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, current.data))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        objectService
            .Setup(service => service.UpdateObjectAsync(
                UserId,
                GraphObjectType.User,
                It.Is<string>(json =>
                    json.Contains($"\"contentId\":\"{contentId}\"", StringComparison.Ordinal) &&
                    json.Contains($"\"mediaId\":\"{mediaId}\"", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
        externalService
            .Setup(service => service.FinalizeMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == croppedUrl &&
                    references[0].ReferenceId == $"socialgraph:user:{UserId}:avatar"),
                UserId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        contentService
            .Setup(service => service.GetContentAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentResult(
                contentId,
                GraphObjectType.FeedPost,
                "source",
                3,
                "2026-01-01T00:00:00Z",
                UserId,
                new[] { new MediaResult(mediaId, GraphMediaType.Photo, sourceUrl) }));
        contentService
            .Setup(service => service.IsAuthorAsync(UserId, contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            contentGraphService: contentService.Object);

        var result = await service.ChangeUserAvatarAsync(
            UserId,
            croppedUrl,
            originalUrl: null,
            privacy: 0,
            sourceContentId: contentId,
            sourceMediaId: mediaId);

        Assert.Equal(croppedUrl, result?.Avatar);
        contentService.Verify(service => service.CreateFeedPostAsync(
            It.IsAny<CreateFeedPostInput>(), It.IsAny<CancellationToken>()), Times.Never);
        objectService.VerifyAll();
        externalService.VerifyAll();
        contentService.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_RejectsForeignOrMismatchedSourceBeforeWriting()
    {
        const long contentId = 501;
        const long mediaId = 502;
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .Setup(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, User(UserId, "Owner").data));
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        contentService
            .Setup(service => service.GetContentAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentResult(
                contentId,
                GraphObjectType.FeedPost,
                "source",
                0,
                "2026-01-01T00:00:00Z",
                UserId + 1,
                new[] { new MediaResult(mediaId, GraphMediaType.Photo, "/media/source.jpg") }));
        contentService
            .Setup(service => service.IsAuthorAsync(UserId, contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            contentGraphService: contentService.Object);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeUserAvatarAsync(
            UserId,
            "/media/crop.jpg",
            originalUrl: null,
            privacy: 0,
            sourceContentId: contentId,
            sourceMediaId: mediaId));

        objectService.Verify(service => service.UpdateObjectAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        contentService.Verify(service => service.GetContentAsync(
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(999, GraphMediaType.Photo)]
    [InlineData(502, GraphMediaType.Video)]
    public async Task ChangeUserAvatar_RejectsMediaOutsideTheSourcePostOrWithWrongType(
        long containedMediaId,
        int containedMediaType)
    {
        const long contentId = 501;
        const long requestedMediaId = 502;
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .Setup(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, User(UserId, "Owner").data));
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        contentService
            .Setup(service => service.IsAuthorAsync(UserId, contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        contentService
            .Setup(service => service.GetContentAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentResult(
                contentId,
                GraphObjectType.FeedPost,
                "source",
                0,
                "2026-01-01T00:00:00Z",
                UserId,
                new[] { new MediaResult(containedMediaId, containedMediaType, "/media/source") }));
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            contentGraphService: contentService.Object);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeUserAvatarAsync(
            UserId,
            "/media/crop.jpg",
            originalUrl: null,
            privacy: 0,
            sourceContentId: contentId,
            sourceMediaId: requestedMediaId));

        objectService.Verify(service => service.UpdateObjectAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        contentService.VerifyAll();
    }

    [Fact]
    public async Task ChangeUserAvatar_RejectsPartialSourcePair()
    {
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .Setup(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, User(UserId, "Owner").data));
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ChangeUserAvatarAsync(
            UserId,
            "/media/crop.jpg",
            originalUrl: null,
            privacy: 0,
            sourceContentId: 123,
            sourceMediaId: null));

        objectService.Verify(service => service.UpdateObjectAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeUserBackground_PublishesTheOriginalUploadAsAPublicCoverActivity()
    {
        const string croppedUrl = "https://cdn.example/cover-cropped.jpg";
        const string originalUrl = "https://cdn.example/cover-original.jpg";
        var current = User(UserId, "Owner");
        var updatedData = JsonSerializer.Serialize(new
        {
            avatar = "",
            background = croppedUrl,
            name = "Owner",
            bio = "",
            gender = 1,
            birthdate = "2000-01-01",
            location = "Ha Noi",
            verify = (string?)null,
            privacy = 0,
            create = "2026-01-01T00:00:00Z"
        });
        var objectService = new Mock<IObjectService>(MockBehavior.Strict);
        objectService
            .SetupSequence(service => service.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, current.data))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        objectService
            .Setup(service => service.UpdateObjectAsync(
                UserId,
                GraphObjectType.User,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, updatedData));
        var externalService = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        externalService
            .Setup(service => service.GetMediaOperationTimeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.Parse("2026-08-07T00:00:00Z"));
        externalService
            .Setup(service => service.FinalizeMediaAsync(
                It.Is<IReadOnlyList<MediaLifecycleReference>>(references =>
                    references.Count == 1 &&
                    references[0].Url == croppedUrl &&
                    references[0].ReferenceId == $"socialgraph:user:{UserId}:background"),
                UserId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var contentService = new Mock<IContentGraphService>(MockBehavior.Strict);
        contentService
            .Setup(service => service.CreateFeedPostAsync(
                It.Is<CreateFeedPostInput>(input =>
                    input.AuthorId == UserId &&
                    input.Content == "tôi đã cập nhật ảnh bìa của mình" &&
                    input.Privacy == 0 &&
                    input.Media != null &&
                    input.Media.Count == 1 &&
                    input.Media[0].Type == GraphMediaType.Photo &&
                    input.Media[0].Url == originalUrl),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentResult(101, GraphObjectType.FeedPost, "tôi đã cập nhật ảnh bìa của mình", 0, "2026-01-01T00:00:00Z", UserId, Array.Empty<MediaResult>()));
        var service = new UserGraphService(
            objectService.Object,
            Mock.Of<IAssociationService>(),
            externalService.Object,
            contentGraphService: contentService.Object);

        var result = await service.ChangeUserBackgroundAsync(UserId, croppedUrl, originalUrl, privacy: 3);

        Assert.NotNull(result);
        Assert.Equal(croppedUrl, result.Background);
        objectService.VerifyAll();
        externalService.VerifyAll();
        contentService.VerifyAll();
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
    public async Task GetFriendProfilesWithMutualCounts_ReturnsProfilesAndBulkMutualCounts()
    {
        const long viewerId = 181;
        const long firstFriendId = 182;
        const long secondFriendId = 183;
        const long firstMutualId = 184;
        const long secondMutualId = 185;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(firstFriendId, "First Friend"),
            User(secondFriendId, "Second Friend"),
            User(firstMutualId, "First Mutual"),
            User(secondMutualId, "Second Mutual"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Friend, firstFriendId),
            Edge(viewerId, GraphAssociationType.Friend, secondFriendId),
            Edge(viewerId, GraphAssociationType.Friend, firstMutualId),
            Edge(viewerId, GraphAssociationType.Friend, secondMutualId),
            Edge(firstFriendId, GraphAssociationType.Friend, viewerId),
            Edge(firstFriendId, GraphAssociationType.Friend, firstMutualId),
            Edge(firstFriendId, GraphAssociationType.Friend, secondMutualId),
            Edge(secondFriendId, GraphAssociationType.Friend, viewerId),
            Edge(secondFriendId, GraphAssociationType.Friend, firstMutualId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var results = await service.GetFriendProfilesWithMutualCountsAsync(viewerId, 100);

        Assert.Equal(4, results.Count);
        Assert.Equal(2, results.Single(item => item.Profile.Id == firstFriendId).MutualFriendCount);
        Assert.Equal(1, results.Single(item => item.Profile.Id == secondFriendId).MutualFriendCount);
    }

    [Fact]
    public async Task GetProfileConnections_LoadsFriendsFollowingAndFollowersForSearchScoping()
    {
        const long viewerId = 191;
        const long friendId = 192;
        const long followingId = 193;
        const long followerId = 194;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(friendId, "Ánh Friend"),
            User(followingId, "Theo Dõi"),
            User(followerId, "Follower"));
        context.AssociationsTb.AddRange(
            Edge(viewerId, GraphAssociationType.Friend, friendId),
            Edge(viewerId, GraphAssociationType.Followed, followingId),
            Edge(viewerId, GraphAssociationType.FollowedBy, followerId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var friendIds = await service.GetProfileConnectionIdsAsync(viewerId, GraphAssociationType.Friend);
        var followingIds = await service.GetProfileConnectionIdsAsync(viewerId, GraphAssociationType.Followed);
        var followerIds = await service.GetProfileConnectionIdsAsync(viewerId, GraphAssociationType.FollowedBy);
        var friends = await service.GetProfileConnectionsAsync(viewerId, GraphAssociationType.Friend, 20);
        var following = await service.GetProfileConnectionsAsync(viewerId, GraphAssociationType.Followed, 20);
        var followers = await service.GetProfileConnectionsAsync(viewerId, GraphAssociationType.FollowedBy, 20);

        Assert.Equal(friendId, Assert.Single(friendIds));
        Assert.Equal(followingId, Assert.Single(followingIds));
        Assert.Equal(followerId, Assert.Single(followerIds));
        Assert.Equal(friendId, Assert.Single(friends).Profile.Id);
        Assert.Equal(followingId, Assert.Single(following).Profile.Id);
        Assert.Equal(followerId, Assert.Single(followers).Profile.Id);
    }

    [Fact]
    public async Task GetProfileFriendsForViewer_FiltersBlockedProfilesAndComputesViewerMutualCounts()
    {
        const long viewerId = 301;
        const long targetUserId = 302;
        const long visibleFriendId = 303;
        const long blockedFriendId = 304;
        const long mutualFriendId = 305;
        const long reverseBlockedFriendId = 306;
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        context.ObjectsTb.AddRange(
            User(viewerId, "Viewer"),
            User(targetUserId, "Target"),
            User(visibleFriendId, "Visible Friend"),
            User(blockedFriendId, "Blocked Friend"),
            User(mutualFriendId, "Mutual Friend"),
            User(reverseBlockedFriendId, "Reverse Blocked Friend"));
        context.AssociationsTb.AddRange(
            Edge(targetUserId, GraphAssociationType.Friend, visibleFriendId),
            Edge(targetUserId, GraphAssociationType.Friend, blockedFriendId),
            Edge(targetUserId, GraphAssociationType.Friend, reverseBlockedFriendId),
            Edge(viewerId, GraphAssociationType.Blocked, blockedFriendId),
            Edge(reverseBlockedFriendId, GraphAssociationType.Blocked, viewerId),
            Edge(viewerId, GraphAssociationType.BlockedBy, reverseBlockedFriendId),
            Edge(viewerId, GraphAssociationType.Friend, mutualFriendId),
            Edge(visibleFriendId, GraphAssociationType.Friend, mutualFriendId));
        await context.SaveChangesAsync();
        var service = new UserGraphService(
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context);

        var result = await service.GetProfileFriendsForViewerAsync(targetUserId, viewerId, 20);

        var visible = Assert.Single(result);
        Assert.Equal(visibleFriendId, visible.Profile.Id);
        Assert.Equal(1, visible.MutualFriendCount);
        Assert.DoesNotContain(result, item => item.Profile.Id == blockedFriendId);
        Assert.DoesNotContain(result, item => item.Profile.Id == reverseBlockedFriendId);
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
            new DateOnly(2000, 1, 1),
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
