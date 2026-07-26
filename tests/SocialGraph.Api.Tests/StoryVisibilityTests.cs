namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

/// <summary>
/// Regression coverage for the story read path.
/// IsStoryShareSourceVisible returned true unconditionally for reels, so a "friends only" or
/// "only me" reel shared into a story was rendered in full to every story viewer.
/// The author list also applied no block filtering at all, so someone who had blocked the viewer
/// still appeared in their story tray. Following an author is, by design, sufficient on its own.
/// </summary>
public sealed class StoryVisibilityTests
{
    private const long ViewerId = 100;
    private const long AuthorId = 200;
    private const long StoryId = 1_000;
    private const long SourceId = 2_000;

    [Theory]
    [InlineData(1)] // friends and current followers
    [InlineData(2)] // friends
    [InlineData(3)] // author only
    public async Task Story_share_of_a_non_public_reel_is_hidden(int privacy)
    {
        await using var context = await SeedSharedReelAsync(privacy);
        var service = CreateService(context, Relations(friends: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Story_share_of_a_public_reel_is_still_visible()
    {
        await using var context = await SeedSharedReelAsync(0);
        var service = CreateService(context, Relations(friends: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        var bucket = Assert.Single(result.Items);
        Assert.NotEmpty(bucket.Stories);
    }

    [Fact]
    public async Task Blocked_authors_stories_are_hidden()
    {
        await using var context = await SeedPlainStoryAsync(authorPrivacy: 0);
        context.AssociationsTb.Add(Edge(ViewerId, GraphAssociationType.Blocked, AuthorId));
        await context.SaveChangesAsync();
        var service = CreateService(context, Relations(friends: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Authors_who_blocked_the_viewer_are_hidden()
    {
        await using var context = await SeedPlainStoryAsync(authorPrivacy: 0);
        context.AssociationsTb.Add(Edge(ViewerId, GraphAssociationType.BlockedBy, AuthorId));
        await context.SaveChangesAsync();
        var service = CreateService(context, Relations(friends: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Following_grants_story_access_regardless_of_author_privacy(int authorPrivacy)
    {
        // Stories reach followers by design; the author's profile privacy governs the profile,
        // not the story tray. Blocks remain the only thing that overrides this.
        await using var context = await SeedPlainStoryAsync(authorPrivacy);
        var service = CreateService(context, Relations(followed: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task Friends_see_stories_regardless_of_author_privacy()
    {
        await using var context = await SeedPlainStoryAsync(authorPrivacy: 2);
        var service = CreateService(context, Relations(friends: [AuthorId]));

        var result = await service.GetHomeStoriesAsync(ViewerId, 20, null);

        Assert.NotEmpty(result.Items);
    }

    private static async Task<MyDbContext> SeedSharedReelAsync(int reelPrivacy)
    {
        var context = CreateContext();
        context.ObjectsTb.AddRange(
            User(AuthorId, authorPrivacy: 0),
            new Objects { id = StoryId, otype = GraphObjectType.Story, data = ActiveStoryJson() },
            new Objects { id = SourceId, otype = GraphObjectType.Reel, data = PostJson(reelPrivacy) });
        context.AssociationsTb.AddRange(
            Edge(AuthorId, GraphAssociationType.Authored, StoryId),
            Edge(StoryId, GraphAssociationType.Share, SourceId));
        await context.SaveChangesAsync();
        return context;
    }

    private static async Task<MyDbContext> SeedPlainStoryAsync(int authorPrivacy)
    {
        var context = CreateContext();
        context.ObjectsTb.AddRange(
            User(AuthorId, authorPrivacy),
            new Objects { id = StoryId, otype = GraphObjectType.Story, data = ActiveStoryJson() });
        context.AssociationsTb.Add(Edge(AuthorId, GraphAssociationType.Authored, StoryId));
        await context.SaveChangesAsync();
        return context;
    }

    private static MyDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static ContentGraphService CreateService(MyDbContext context, Mock<IAssociationService> associations) =>
        new(context, new Mock<IObjectService>(MockBehavior.Loose).Object, associations.Object, Mock.Of<IExternalServiceClient>());

    private static Mock<IAssociationService> Relations(long[]? friends = null, long[]? followed = null)
    {
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        Setup(GraphAssociationType.Friend, friends ?? []);
        Setup(GraphAssociationType.Followed, followed ?? []);
        return associations;

        void Setup(short associationType, long[] ids) =>
            associations
                .Setup(item => item.RetrieveAssociationAsync(
                    ViewerId,
                    associationType,
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AssociationPageResult(
                    ids.Select(id => new AssociationEdgeResult(id, 1)).ToArray(),
                    null));
    }

    private static Objects User(long id, int authorPrivacy) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = new JsonObject
        {
            ["name"] = "Story Author",
            ["avatar"] = "",
            ["verify"] = "",
            ["privacy"] = authorPrivacy
        }.ToJsonString()
    };

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private static string ActiveStoryJson() => new JsonObject
    {
        ["content"] = "story",
        ["create"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),
        ["expire"] = DateTimeOffset.UtcNow.AddHours(23).ToString("O")
    }.ToJsonString();

    private static string PostJson(int privacy) => new JsonObject
    {
        ["content"] = "source",
        ["privacy"] = privacy,
        ["create"] = DateTimeOffset.UtcNow.ToString("O")
    }.ToJsonString();
}
