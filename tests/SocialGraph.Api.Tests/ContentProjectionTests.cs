namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using System.Text.Json.Nodes;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class ContentProjectionTests
{
    private const long AuthorId = 9_000_000_000_000_001;
    private const long ReelId = 9_000_000_000_000_002;
    private const long PostId = 9_000_000_000_000_003;
    private const long SourceId = 9_000_000_000_000_004;
    private const long SourceAuthorId = 9_000_000_000_000_005;
    private const long MentionedUserId = 9_000_000_000_000_006;
    private const long LegacyMentionedUserId = 9_000_000_000_000_007;

    [Fact]
    public async Task GroupAdministrator_CanDeleteOnlyPostsPublishedInTheirGroup()
    {
        await using var context = CreateContext();
        const long groupId = 9_000_000_000_000_020;
        const long adminId = 9_000_000_000_000_021;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.GroupPost, ContentJson("group post")));
        objects.Setup(item => item.RetrieveObjectAsync(ReelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(ReelId, GraphObjectType.Reel, ReelJson("reel", 0, 1d, 0.5d, 0.5d)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.PublishedIn, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(groupId, 1) }, null));
        associations.Setup(item => item.HasAssociationAsync(adminId, GraphAssociationType.Admin, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        Assert.True(await service.CanDeleteContentAsync(adminId, PostId));
        Assert.False(await service.CanDeleteContentAsync(adminId, ReelId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CreateGroupPost_RejectsReferencesUnlessTheyAreFriendAndParticipant(
        bool isFriend,
        bool isParticipant)
    {
        await using var context = CreateContext();
        const long groupId = 9_000_000_000_000_030;
        const long targetId = 9_000_000_000_000_031;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(AuthorId, GraphAssociationType.Friend, targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(isFriend);
        associations.Setup(item => item.HasAssociationAsync(targetId, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(isParticipant);
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateGroupPostAsync(
            new CreateGroupPostInput(
                AuthorId,
                groupId,
                $"Hello [[mention:{targetId}]]",
                null,
                TaggedUserIds: new[] { targetId })));

        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateGroupPost_PersistsEligibleTagAndMentionReferences()
    {
        await using var context = CreateContext();
        const long groupId = 9_000_000_000_000_040;
        const long targetId = 9_000_000_000_000_041;
        var text = $"Hello [[mention:{targetId}]]";
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.GroupPost, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.GroupPost, ContentJson(text)));
        objects.Setup(item => item.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, new JsonObject { ["privacy"] = 1 }.ToJsonString()));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.HasAssociationAsync(AuthorId, GraphAssociationType.Friend, targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(AuthorId, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.HasAssociationAsync(targetId, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.PublishedIn, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(groupId, 1) }, null));
        associations.Setup(item => item.AddAssociationAsync(PostId, GraphAssociationType.Tagged, targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.AddAssociationAsync(PostId, GraphAssociationType.Mentioned, targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        var created = await service.CreateGroupPostAsync(new CreateGroupPostInput(
            AuthorId,
            groupId,
            text,
            null,
            TaggedUserIds: new[] { targetId }));

        Assert.Equal(1, created.Privacy);
        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Tagged, targetId, It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Mentioned, targetId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SharePost_RejectsAStaleDestinationGroupMembershipBeforeWriting()
    {
        await using var context = CreateContext();
        const long groupId = 9_000_000_000_000_042;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SharePostAsync(
            new SharePostInput(AuthorId, SourceId, "share", 0, DestinationGroupId: groupId)));

        Assert.Contains("current group members", exception.Message, StringComparison.OrdinalIgnoreCase);
        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectGroupReference_RejectsAccountsOutsideFriendAndParticipantIntersection(bool tag)
    {
        await using var context = CreateContext();
        const long groupId = 9_000_000_000_000_050;
        const long targetId = 9_000_000_000_000_051;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.GroupPost, ContentJson("group post")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.PublishedIn, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(groupId, 1) }, null));
        associations.Setup(item => item.HasAssociationAsync(AuthorId, GraphAssociationType.Friend, targetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => tag
            ? service.TagAsync(PostId, targetId)
            : service.MentionAsync(PostId, targetId));

        associations.Verify(item => item.AddAssociationAsync(
            PostId,
            tag ? GraphAssociationType.Tagged : GraphAssociationType.Mentioned,
            targetId,
            It.IsAny<CancellationToken>()), Times.Never);
    }


    [Fact]
    public async Task CreateFeedPost_DerivesMentionsOnlyFromContentTokens()
    {
        await using var context = CreateContext();
        var content = $"Hello [[mention:{MentionedUserId}]]";
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.FeedPost, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson(content, 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(PostId, GraphAssociationType.Mentioned, MentionedUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        await service.CreateFeedPostAsync(new CreateFeedPostInput(
            AuthorId,
            content,
            0,
            null,
            MentionedUserIds: new[] { LegacyMentionedUserId }));

        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Mentioned, MentionedUserId, It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Mentioned, LegacyMentionedUserId, It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.NotifyAsync(
            AuthorId,
            MentionedUserId,
            ExternalNotificationAction.Mention,
            PostId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateReel_ProjectsToSearchAndRecommendation()
    {
        await using var context = CreateContext();
        string? storedData = null;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Reel, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<short, string, CancellationToken>((_, data, _) => storedData = data)
            .ReturnsAsync(new SocialGraphObjectResult(
                ReelId,
                GraphObjectType.Reel,
                ReelJson("canonical reel", 2, 16d / 9d, 0.2d, 0.8d)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Authored, ReelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var reel = await service.CreateReelAsync(new CreateReelInput(
            AuthorId,
            "canonical reel",
            null,
            Privacy: 2,
            AspectRatio: 16d / 9d,
            FocalPointX: 0.2d,
            FocalPointY: 0.8d));

        Assert.Equal(ReelId, reel.Id);
        Assert.Equal(2, reel.Privacy);
        Assert.NotNull(reel.AspectRatio);
        Assert.Equal(16d / 9d, reel.AspectRatio.GetValueOrDefault(), precision: 6);
        Assert.Equal(0.2d, reel.FocalPointX);
        Assert.Equal(0.8d, reel.FocalPointY);
        Assert.Equal(2, JsonNode.Parse(storedData!)!["privacy"]!.GetValue<int>());
        Assert.Equal(16d / 9d, JsonNode.Parse(storedData!)!["aspectRatio"]!.GetValue<double>(), precision: 6);
        Assert.Equal(0.2d, JsonNode.Parse(storedData!)!["focalPointX"]!.GetValue<double>());
        Assert.Equal(0.8d, JsonNode.Parse(storedData!)!["focalPointY"]!.GetValue<double>());
        external.Verify(item => item.CreateSearchIndexAsync(ReelId, "reel", "canonical reel", It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.CreatePostEmbeddingAsync(ReelId, "canonical reel", It.Is<IReadOnlyList<string>>(urls => urls.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateComment_AllowsOneImageAndFinalizesIt()
    {
        await using var context = CreateContext();
        const long commentId = 9_000_000_000_000_008;
        const long mediaId = 9_000_000_000_000_009;
        const string mediaUrl = "https://cdn.example/comment.jpg";
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Comment, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(commentId, GraphObjectType.Comment, ContentJson(string.Empty)));
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Media, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(mediaId, GraphObjectType.Media, MediaJson(0, mediaUrl)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var comment = await service.CreateCommentAsync(new CreateCommentInput(
            AuthorId,
            PostId,
            string.Empty,
            new MediaInput(0, mediaUrl)));

        Assert.Equal(mediaId, Assert.Single(comment.Media).Id);
        associations.Verify(item => item.AddAssociationAsync(
            commentId,
            GraphAssociationType.Contained,
            mediaId,
            It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.FinalizeMediaAsync(
            It.Is<IReadOnlyList<string>>(urls => urls.SequenceEqual(new[] { mediaUrl })),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CreateComment_RejectsNonImageMedia(int mediaType)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var service = new ContentGraphService(
            context,
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateCommentAsync(
            new CreateCommentInput(AuthorId, PostId, string.Empty, new MediaInput(mediaType, "https://cdn.example/file"))));
        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task CreateReel_RejectsPrivacyOutsideTheFourFeedAudiences(int privacy)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var service = new ContentGraphService(
            context,
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.CreateReelAsync(new CreateReelInput(AuthorId, "reel", null, privacy)));
        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.8)]
    public async Task CreateReel_RejectsAspectRatioOutsideSupportedPresentationRange(double aspectRatio)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var service = new ContentGraphService(
            context,
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateReelAsync(
            new CreateReelInput(AuthorId, "reel", null, Privacy: 0, AspectRatio: aspectRatio)));
        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(1.01, 0.5)]
    [InlineData(0.5, -0.01)]
    [InlineData(0.5, 1.01)]
    public async Task CreateReel_RejectsFocalPointOutsideNormalizedRange(double focalPointX, double focalPointY)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        var service = new ContentGraphService(
            context,
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateReelAsync(
            new CreateReelInput(
                AuthorId,
                "reel",
                null,
                Privacy: 0,
                AspectRatio: 1d,
                FocalPointX: focalPointX,
                FocalPointY: focalPointY)));
        objects.Verify(item => item.AddObjectAsync(
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteReel_RemovesSearchAndRecommendationProjections()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(ReelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(ReelId, GraphObjectType.Reel, ContentJson("deleted reel")));
        objects.Setup(item => item.DeleteObjectAsync(ReelId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var deleted = await service.DeleteContentAsync(ReelId);

        Assert.True(deleted);
        external.Verify(item => item.DeleteSearchIndexAsync(ReelId, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.DeletePostEmbeddingAsync(ReelId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdatePost_UpdatesContentAndReplacesMediaWithoutOverwritingOmittedPrivacy()
    {
        await using var context = CreateContext();
        const long oldMediaId = 81;
        const long newMediaId = 82;
        string? patch = null;
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson("old", 1)));
        objects.Setup(item => item.UpdateObjectAsync(PostId, GraphObjectType.FeedPost, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<long, short, string, CancellationToken>((_, _, value, _) => patch = value)
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson("updated", 1)));
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Media, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(newMediaId, GraphObjectType.Media, MediaJson(0, "https://cdn/new.jpg")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.Contained, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(oldMediaId, 1) }, null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var result = await service.UpdatePostAsync(new UpdatePostInput(
            PostId,
            Content: "updated",
            Media: new[] { new MediaInput(0, "https://cdn/new.jpg") }));

        Assert.NotNull(result);
        Assert.Equal(1, result.Privacy);
        var patchJson = JsonNode.Parse(patch!)!.AsObject();
        Assert.Equal("updated", patchJson["content"]!.GetValue<string>());
        Assert.False(patchJson.ContainsKey("privacy"));
        associations.Verify(item => item.DeleteOneAssociationAsync(PostId, GraphAssociationType.Contained, oldMediaId, It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.AddAssociationAsync(PostId, GraphAssociationType.Contained, newMediaId, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.UpdateSearchIndexAsync(PostId, "feedPost", "updated", It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.CreatePostEmbeddingAsync(
            PostId,
            "updated",
            It.Is<IReadOnlyList<string>>(urls => urls.SequenceEqual(new[] { "https://cdn/new.jpg" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(GraphObjectType.FeedPost, -1)]
    [InlineData(GraphObjectType.FeedPost, 4)]
    [InlineData(GraphObjectType.Reel, -1)]
    [InlineData(GraphObjectType.Reel, 4)]
    public async Task UpdatePost_RejectsPrivacyOutsideTheFourFeedAudiences(short objectType, int privacy)
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, objectType, PostJson("post", 0)));
        var service = new ContentGraphService(
            context,
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.UpdatePostAsync(new UpdatePostInput(PostId, Privacy: privacy)));
        objects.Verify(item => item.UpdateObjectAsync(
            It.IsAny<long>(),
            It.IsAny<short>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SharePost_NotifiesSourceAuthorButNeverSelfNotifies()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.FeedPost, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson("share", 0)));
        objects.Setup(item => item.RetrieveObjectAsync(SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(SourceId, GraphObjectType.FeedPost, PostJson("source", 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Authored, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.AddAssociationAsync(PostId, GraphAssociationType.Share, SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(SourceId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(SourceAuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.Contained, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        await service.SharePostAsync(new SharePostInput(AuthorId, SourceId, "share", 0));

        external.Verify(item => item.NotifyAsync(
            AuthorId,
            SourceAuthorId,
            ExternalNotificationAction.Share,
            SourceId,
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.NotifyAsync(
            It.IsAny<long>(),
            AuthorId,
            ExternalNotificationAction.Share,
            It.IsAny<long?>(),
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId,
            SourceId,
            RecommendationInteractionAction.Share,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SharePost_FlattensAnExistingShareWrapperToItsOriginalSource()
    {
        await using var context = CreateContext();
        const long wrapperId = 9_000_000_000_000_008;
        context.ObjectsTb.Add(new Objects
        {
            id = wrapperId,
            otype = GraphObjectType.FeedPost,
            data = PostJson("wrapper", 0)
        });
        context.AssociationsTb.Add(new Associations
        {
            id1 = wrapperId,
            atype = GraphAssociationType.Share,
            id2 = SourceId,
            time = 1
        });
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.FeedPost, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson("share", 0)));
        objects.Setup(item => item.RetrieveObjectAsync(SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(SourceId, GraphObjectType.FeedPost, PostJson("source", 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Authored, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.AddAssociationAsync(PostId, GraphAssociationType.Share, SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(AuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(SourceId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(SourceAuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.Contained, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        await service.SharePostAsync(new SharePostInput(AuthorId, wrapperId, "share", 0));

        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Share, SourceId, It.IsAny<CancellationToken>()), Times.Once);
        associations.Verify(item => item.AddAssociationAsync(
            PostId, GraphAssociationType.Share, wrapperId, It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, SourceId, RecommendationInteractionAction.Share, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CanonicalShareSource_UnwrapsASharedGroupWrapperToTheGroup()
    {
        await using var context = CreateContext();
        const long wrapperId = 9_000_000_000_000_010;
        const long groupId = 9_000_000_000_000_011;
        context.ObjectsTb.AddRange(
            new Objects { id = wrapperId, otype = GraphObjectType.FeedPost, data = PostJson("shared group", 0) },
            new Objects { id = groupId, otype = GraphObjectType.Group, data = new JsonObject { ["name"] = "Group" }.ToJsonString() });
        context.AssociationsTb.Add(new Associations
        {
            id1 = wrapperId,
            atype = GraphAssociationType.Share,
            id2 = groupId,
            time = 1
        });
        await context.SaveChangesAsync();
        var service = new ContentGraphService(
            context,
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        Assert.Equal(groupId, await service.ResolveCanonicalShareSourceIdAsync(wrapperId));
    }

    [Fact]
    public async Task CanonicalShareSource_DoesNotUnwrapAStoryShareEdge()
    {
        await using var context = CreateContext();
        const long storyId = 9_000_000_000_000_009;
        context.ObjectsTb.Add(new Objects
        {
            id = storyId,
            otype = GraphObjectType.Story,
            data = ContentJson("story")
        });
        context.AssociationsTb.Add(new Associations
        {
            id1 = storyId,
            atype = GraphAssociationType.Share,
            id2 = SourceId,
            time = 1
        });
        await context.SaveChangesAsync();
        var service = new ContentGraphService(
            context,
            Mock.Of<IObjectService>(),
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>());

        Assert.Equal(storyId, await service.ResolveCanonicalShareSourceIdAsync(storyId));
    }

    [Fact]
    public async Task ShareStory_NotifiesSourceAuthorThroughCanonicalShareAction()
    {
        await using var context = CreateContext();
        const long storyId = 9_000_000_000_000_006;
        context.ObjectsTb.AddRange(
            new Objects { id = SourceId, otype = GraphObjectType.FeedPost, data = PostJson("source", 0) },
            new Objects { id = SourceAuthorId, otype = GraphObjectType.User, data = UserJson("Source author") });
        context.AssociationsTb.Add(new Associations
        {
            id1 = SourceId,
            atype = GraphAssociationType.AuthoredBy,
            id2 = SourceAuthorId,
            time = 1
        });
        await context.SaveChangesAsync();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(SourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(SourceId, GraphObjectType.FeedPost, PostJson("source", 0)));
        objects.Setup(item => item.RetrieveObjectAsync(SourceAuthorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(SourceAuthorId, GraphObjectType.User, UserJson("Source author")));
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Story, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(storyId, GraphObjectType.Story, ContentJson("story share")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(SourceId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(new[] { new AssociationEdgeResult(SourceAuthorId, 1) }, null));
        associations.Setup(item => item.RetrieveAssociationAsync(SourceId, GraphAssociationType.Contained, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        await service.CreateShareStoryAsync(new CreateShareStoryInput(AuthorId, "story share", SourceId));

        external.Verify(item => item.NotifyAsync(
            AuthorId,
            SourceAuthorId,
            ExternalNotificationAction.Share,
            SourceId,
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId,
            SourceId,
            RecommendationInteractionAction.Share,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EngagementFeedback_IsQueuedOnlyWhenCanonicalStateChanges()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(PostId, GraphObjectType.FeedPost, PostJson("source", 0)));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Liked, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.DeleteOneAssociationAsync(AuthorId, GraphAssociationType.Liked, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Saved, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(item => item.DeleteOneAssociationAsync(AuthorId, GraphAssociationType.Saved, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Watched, PostId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        associations.Setup(item => item.RetrieveAssociationAsync(PostId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        Assert.True(await service.LikeAsync(AuthorId, PostId));
        Assert.True(await service.UnlikeAsync(AuthorId, PostId));
        Assert.True(await service.SaveAsync(AuthorId, PostId));
        Assert.False(await service.UnsaveAsync(AuthorId, PostId));
        Assert.False(await service.WatchAsync(AuthorId, PostId));

        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, PostId, RecommendationInteractionAction.Like, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, PostId, RecommendationInteractionAction.Unlike, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, PostId, RecommendationInteractionAction.Save, It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, PostId, RecommendationInteractionAction.Unsave, It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(item => item.RecordRecommendationInteractionAsync(
            AuthorId, PostId, RecommendationInteractionAction.Watch, It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MyDbContext(options);
    }

    private static string ContentJson(string content) => new JsonObject
    {
        ["content"] = content,
        ["create"] = DateTimeOffset.UtcNow.ToString("O")
    }.ToJsonString();

    private static string PostJson(string content, int privacy) => new JsonObject
    {
        ["content"] = content,
        ["privacy"] = privacy,
        ["create"] = DateTimeOffset.UtcNow.ToString("O")
    }.ToJsonString();

    private static string ReelJson(string content, int privacy, double aspectRatio, double focalPointX, double focalPointY) => new JsonObject
    {
        ["content"] = content,
        ["privacy"] = privacy,
        ["create"] = DateTimeOffset.UtcNow.ToString("O"),
        ["aspectRatio"] = aspectRatio,
        ["focalPointX"] = focalPointX,
        ["focalPointY"] = focalPointY
    }.ToJsonString();

    private static string MediaJson(int type, string url) => new JsonObject
    {
        ["type"] = type,
        ["url"] = url
    }.ToJsonString();

    private static string UserJson(string name) => new JsonObject
    {
        ["name"] = name,
        ["avatar"] = "",
        ["verify"] = ""
    }.ToJsonString();
}
