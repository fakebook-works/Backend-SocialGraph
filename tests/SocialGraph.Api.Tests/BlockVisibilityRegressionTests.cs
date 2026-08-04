namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class BlockVisibilityRegressionTests
{
    private const long ViewerId = 10;
    private const long BlockedUserId = 20;
    private const long VisibleUserId = 30;

    [Theory]
    [InlineData(GraphAssociationType.Blocked)]
    [InlineData(GraphAssociationType.BlockedBy)]
    public async Task GroupMemberProjection_HidesBlocksInEitherDirection(short blockType)
    {
        await using var context = CreateContext();
        const long groupId = 40;
        context.ObjectsTb.AddRange(User(BlockedUserId, "Hidden"), User(VisibleUserId, "Visible"));
        context.AssociationsTb.Add(Edge(ViewerId, blockType, BlockedUserId));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>();
        objects.Setup(service => service.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, new JsonObject { ["privacy"] = 0 }.ToJsonString()));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(service => service.RetrieveAssociationAsync(
                groupId, GraphAssociationType.HaveMember, null, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(
                [new AssociationEdgeResult(BlockedUserId, 2), new AssociationEdgeResult(VisibleUserId, 1)],
                null));
        var service = new SocialReadModelService(
            context,
            objects.Object,
            associations.Object,
            new Mock<IContentGraphService>().Object);

        var page = await service.GetGroupMembersAsync(ViewerId, groupId, null, 20, admins: false);

        Assert.Equal(VisibleUserId, Assert.Single(page.Items).Id);
    }

    [Theory]
    [InlineData(GraphAssociationType.Blocked)]
    [InlineData(GraphAssociationType.BlockedBy)]
    public async Task GroupMemberProjection_KeepsBlockedUsersForCurrentGroupParticipants(short blockType)
    {
        await using var context = CreateContext();
        const long groupId = 41;
        context.ObjectsTb.Add(User(BlockedUserId, "Shared group member"));
        context.AssociationsTb.Add(Edge(ViewerId, blockType, BlockedUserId));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>();
        objects.Setup(service => service.RetrieveObjectAsync(groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(groupId, GraphObjectType.Group, new JsonObject { ["privacy"] = 1 }.ToJsonString()));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(service => service.HasAssociationAsync(
                ViewerId, GraphAssociationType.Member, groupId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        associations.Setup(service => service.RetrieveAssociationAsync(
                groupId, GraphAssociationType.HaveMember, null, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult([new AssociationEdgeResult(BlockedUserId, 1)], null));
        var service = new SocialReadModelService(
            context,
            objects.Object,
            associations.Object,
            new Mock<IContentGraphService>().Object);

        var page = await service.GetGroupMembersAsync(ViewerId, groupId, null, 20, admins: false);

        Assert.Equal(BlockedUserId, Assert.Single(page.Items).Id);
    }

    [Theory]
    [InlineData(GraphAssociationType.Blocked)]
    [InlineData(GraphAssociationType.BlockedBy)]
    public async Task CommentProjection_HidesBlockedAuthors(short blockType)
    {
        await using var context = CreateContext();
        const long postId = 50;
        const long hiddenCommentId = 51;
        const long visibleCommentId = 52;
        context.ObjectsTb.AddRange(
            User(BlockedUserId, "Hidden"),
            User(VisibleUserId, "Visible"),
            Comment(hiddenCommentId, "hidden"),
            Comment(visibleCommentId, "visible"));
        context.AssociationsTb.AddRange(
            Edge(ViewerId, blockType, BlockedUserId),
            Edge(hiddenCommentId, GraphAssociationType.AuthoredBy, BlockedUserId),
            Edge(visibleCommentId, GraphAssociationType.AuthoredBy, VisibleUserId));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>();
        objects.Setup(service => service.RetrieveObjectAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(postId, GraphObjectType.FeedPost, PostJson("post")));
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(service => service.RetrieveAssociationAsync(
                postId, GraphAssociationType.HaveComment, null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(
                [new AssociationEdgeResult(hiddenCommentId, 2), new AssociationEdgeResult(visibleCommentId, 1)],
                null));
        var content = new Mock<IContentGraphService>();
        content.Setup(service => service.GetPostDetailAsync(ViewerId, postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FeedPostDetailResult(
                postId,
                GraphObjectType.FeedPost,
                "post",
                0,
                "now",
                new PostAuthorResult(99, "Author", "", false, false),
                []));
        var service = new SocialReadModelService(context, objects.Object, associations.Object, content.Object);

        var page = await service.GetCommentsAsync(ViewerId, postId, null, 20);

        var comment = Assert.Single(page.Items);
        Assert.Equal(visibleCommentId, comment.Id);
        Assert.Equal("Visible", comment.Author.Name);
    }

    [Fact]
    public async Task CreatingPost_RejectsBlockedTagBeforeWritingOrNotifying()
    {
        await using var context = CreateContext();
        context.AssociationsTb.Add(Edge(ViewerId, GraphAssociationType.BlockedBy, BlockedUserId));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Strict);
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateFeedPostAsync(new CreateFeedPostInput(
                ViewerId,
                "post",
                0,
                [],
                [BlockedUserId])));

        Assert.Equal("A blocked account cannot be tagged or mentioned.", error.Message);
        objects.VerifyNoOtherCalls();
        associations.VerifyNoOtherCalls();
        external.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(GraphAssociationType.Blocked)]
    [InlineData(GraphAssociationType.BlockedBy)]
    public async Task DirectTagMutation_DoesNotCreateAnEdgeOrNotificationAcrossBlock(short blockType)
    {
        await using var context = CreateContext();
        const long postId = 60;
        context.AssociationsTb.Add(Edge(ViewerId, blockType, BlockedUserId));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(service => service.RetrieveObjectAsync(postId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(postId, GraphObjectType.FeedPost, "{}"));
        var associations = new Mock<IAssociationService>(MockBehavior.Strict);
        associations.Setup(service => service.RetrieveAssociationAsync(
                postId, GraphAssociationType.AuthoredBy, null, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult([new AssociationEdgeResult(ViewerId, 1)], null));
        var external = new Mock<IExternalServiceClient>(MockBehavior.Strict);
        var service = new ContentGraphService(context, objects.Object, associations.Object, external.Object);

        Assert.False(await service.TagAsync(postId, BlockedUserId));

        associations.Verify(service => service.AddAssociationAsync(
            It.IsAny<long>(), It.IsAny<short>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        external.Verify(service => service.NotifyAsync(
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<short>(), It.IsAny<long?>(), It.IsAny<object?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MyDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static Objects User(long id, string name) => new()
    {
        id = id,
        otype = GraphObjectType.User,
        data = new JsonObject
        {
            ["name"] = name,
            ["avatar"] = "",
            ["verify"] = "",
            ["privacy"] = 0
        }.ToJsonString()
    };

    private static Objects Comment(long id, string content) => new()
    {
        id = id,
        otype = GraphObjectType.Comment,
        data = PostJson(content)
    };

    private static string PostJson(string content) => new JsonObject
    {
        ["content"] = content,
        ["privacy"] = 0,
        ["create"] = "now"
    }.ToJsonString();

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = 1
    };
}
