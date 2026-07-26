namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

/// <summary>
/// Covers the two gaps that stopped the SPA from finishing comment features without backend work:
/// a like or mention notification on a comment carries the comment's id, and there was no way to
/// turn that into the post it belongs to, so every such deep link rendered "content unavailable";
/// and there was no mutation to edit a comment at all.
/// </summary>
public sealed class CommentApiTests
{
    private const long PostId = 5_000;
    private const long CommentId = 6_000;
    private const long ReplyId = 7_000;
    private const long AuthorId = 200;

    [Fact]
    public async Task Root_post_resolves_through_a_comment()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, CommentId, GraphObjectType.Comment);
        Setup(objects, PostId, GraphObjectType.FeedPost);
        var associations = new Mock<IAssociationService>();
        SetupParent(associations, CommentId, PostId);
        var service = CreateService(objects, associations);

        Assert.Equal(PostId, await service.ResolveRootPostIdAsync(CommentId));
    }

    [Fact]
    public async Task Root_post_resolves_through_a_nested_reply()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, ReplyId, GraphObjectType.Comment);
        Setup(objects, CommentId, GraphObjectType.Comment);
        Setup(objects, PostId, GraphObjectType.Reel);
        var associations = new Mock<IAssociationService>();
        SetupParent(associations, ReplyId, CommentId);
        SetupParent(associations, CommentId, PostId);
        var service = CreateService(objects, associations);

        Assert.Equal(PostId, await service.ResolveRootPostIdAsync(ReplyId));
    }

    [Fact]
    public async Task Root_post_of_a_post_is_itself()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, PostId, GraphObjectType.GroupPost);
        var service = CreateService(objects, new Mock<IAssociationService>());

        Assert.Equal(PostId, await service.ResolveRootPostIdAsync(PostId));
    }

    [Fact]
    public async Task Root_post_is_zero_for_an_orphan_comment()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, CommentId, GraphObjectType.Comment);
        var associations = new Mock<IAssociationService>();
        SetupParent(associations, CommentId, 0);
        var service = CreateService(objects, associations);

        Assert.Equal(0, await service.ResolveRootPostIdAsync(CommentId));
    }

    [Fact]
    public async Task Root_post_is_zero_for_unknown_content()
    {
        var service = CreateService(new Mock<IObjectService>(), new Mock<IAssociationService>());

        Assert.Equal(0, await service.ResolveRootPostIdAsync(123));
    }

    [Fact]
    public async Task Root_post_resolution_stops_on_a_cycle()
    {
        // A malformed graph must not spin forever.
        var objects = new Mock<IObjectService>();
        Setup(objects, CommentId, GraphObjectType.Comment);
        Setup(objects, ReplyId, GraphObjectType.Comment);
        var associations = new Mock<IAssociationService>();
        SetupParent(associations, CommentId, ReplyId);
        SetupParent(associations, ReplyId, CommentId);
        var service = CreateService(objects, associations);

        Assert.Equal(0, await service.ResolveRootPostIdAsync(CommentId));
    }

    [Fact]
    public async Task Update_comment_refuses_anything_that_is_not_a_comment()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, PostId, GraphObjectType.FeedPost);
        var service = CreateService(objects, new Mock<IAssociationService>());

        Assert.Null(await service.UpdateCommentAsync(new UpdateCommentInput(PostId, "edited")));
    }

    [Fact]
    public async Task Update_comment_rejects_media_that_is_not_a_single_image()
    {
        var objects = new Mock<IObjectService>();
        Setup(objects, CommentId, GraphObjectType.Comment);
        var service = CreateService(objects, new Mock<IAssociationService>());

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateCommentAsync(
            new UpdateCommentInput(CommentId, "edited", new MediaInput(GraphMediaType.Video, "/media/files/a.mp4"))));
    }

    private static void Setup(Mock<IObjectService> objects, long id, short objectType) =>
        objects
            .Setup(item => item.RetrieveObjectAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(id, objectType, "{}"));

    private static void SetupParent(Mock<IAssociationService> associations, long childId, long parentId) =>
        associations
            .Setup(item => item.RetrieveAssociationAsync(
                childId,
                GraphAssociationType.Comment,
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(
                parentId <= 0
                    ? Array.Empty<AssociationEdgeResult>()
                    : new[] { new AssociationEdgeResult(parentId, 1) },
                null));

    private static ContentGraphService CreateService(
        Mock<IObjectService> objects,
        Mock<IAssociationService> associations) =>
        new(
            new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options),
            objects.Object,
            associations.Object,
            Mock.Of<IExternalServiceClient>());
}
