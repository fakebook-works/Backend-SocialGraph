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

    [Fact]
    public async Task CreateReel_ProjectsToSearchAndRecommendation()
    {
        await using var context = CreateContext();
        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.AddObjectAsync(GraphObjectType.Reel, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(
                ReelId,
                GraphObjectType.Reel,
                ContentJson("canonical reel")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.AddAssociationAsync(AuthorId, GraphAssociationType.Authored, ReelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var reel = await service.CreateReelAsync(new CreateReelInput(AuthorId, "canonical reel", null));

        Assert.Equal(ReelId, reel.Id);
        external.Verify(item => item.CreateSearchIndexAsync(ReelId, "reel", "canonical reel", It.IsAny<CancellationToken>()), Times.Once);
        external.Verify(item => item.CreatePostEmbeddingAsync(ReelId, "canonical reel", It.Is<IReadOnlyList<string>>(urls => urls.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ShareStory_NotifiesSourceAuthorThroughCanonicalShareAction()
    {
        await using var context = CreateContext();
        const long storyId = 9_000_000_000_000_006;
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
