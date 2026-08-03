namespace SocialGraph.Api.Tests;

using System.Text.Json.Nodes;
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

    [Fact]
    public async Task Update_comment_persists_text_and_keeps_only_the_latest_twenty_revisions()
    {
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var originalData = GraphJson.ContentJson("version-0");
        context.ObjectsTb.Add(new Objects { id = CommentId, otype = GraphObjectType.Comment, data = originalData });
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(CommentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(CommentId, GraphObjectType.Comment, originalData));
        objects.Setup(item => item.InvalidateObjectCacheAsync(CommentId)).Returns(Task.CompletedTask);
        var associations = EmptyAssociations();
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        for (var version = 1; version <= 22; version++)
        {
            var updated = await service.UpdateCommentAsync(new UpdateCommentInput(CommentId, $"version-{version}"));
            Assert.Equal($"version-{version}", updated?.Content);
        }

        var data = GraphJson.ParseObject((await context.ObjectsTb.SingleAsync()).data);
        var history = GraphJson.CommentEditHistory(data);
        Assert.Equal("version-22", GraphJson.String(data, "content"));
        Assert.Equal(20, history.Count);
        Assert.Equal("version-2", history[0].Content);
        Assert.Equal("version-21", history[^1].Content);

        await service.UpdateCommentAsync(new UpdateCommentInput(CommentId, "version-22"));
        Assert.Equal(20, GraphJson.CommentEditHistory(GraphJson.ParseObject((await context.ObjectsTb.SingleAsync()).data)).Count);
        objects.Verify(item => item.UpdateObjectAsync(
            It.IsAny<long>(),
            GraphObjectType.Comment,
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Comment_edit_history_records_when_each_version_became_current()
    {
        var data = new JsonObject
        {
            ["content"] = "first",
            ["create"] = "2026-08-03T01:00:00Z"
        };

        Assert.True(GraphJson.ApplyCommentEdit(data, "second", "2026-08-03T02:00:00Z"));
        Assert.True(GraphJson.ApplyCommentEdit(data, "third", "2026-08-03T03:00:00Z"));

        var history = GraphJson.CommentEditHistory(data);
        Assert.Collection(
            history,
            first =>
            {
                Assert.Equal("first", first.Content);
                Assert.Equal("2026-08-03T01:00:00Z", first.EditedAt);
            },
            second =>
            {
                Assert.Equal("second", second.Content);
                Assert.Equal("2026-08-03T02:00:00Z", second.EditedAt);
            });
        Assert.Equal("2026-08-03T03:00:00Z", GraphJson.NullableString(data, "editedAt"));
    }

    [Fact]
    public async Task Delete_comment_writes_an_idempotent_tombstone_without_removing_the_reply_tree()
    {
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var originalData = new JsonObject
        {
            ["content"] = "private text",
            ["create"] = "2026-08-03T00:00:00Z",
            ["editedAt"] = "2026-08-03T00:01:00Z",
            ["editHistory"] = new JsonArray(new JsonObject
            {
                ["content"] = "older private text",
                ["editedAt"] = "2026-08-03T00:01:00Z"
            })
        }.ToJsonString();
        context.ObjectsTb.Add(new Objects { id = CommentId, otype = GraphObjectType.Comment, data = originalData });
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(CommentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(CommentId, GraphObjectType.Comment, originalData));
        objects.Setup(item => item.InvalidateObjectCacheAsync(CommentId)).Returns(Task.CompletedTask);
        var associations = EmptyAssociations();
        var service = new ContentGraphService(context, objects.Object, associations.Object, Mock.Of<IExternalServiceClient>());

        Assert.True(await service.DeleteContentAsync(CommentId));
        var first = GraphJson.ParseObject((await context.ObjectsTb.SingleAsync()).data);
        var deletedAt = GraphJson.NullableString(first, "deletedAt");
        Assert.True(GraphJson.IsCommentDeleted(first));
        Assert.Equal(string.Empty, GraphJson.String(first, "content"));
        Assert.False(first.ContainsKey("editedAt"));
        Assert.False(first.ContainsKey("editHistory"));

        Assert.True(await service.DeleteContentAsync(CommentId));
        var second = GraphJson.ParseObject((await context.ObjectsTb.SingleAsync()).data);
        Assert.Equal(deletedAt, GraphJson.NullableString(second, "deletedAt"));
        objects.Verify(item => item.DeleteObjectAsync(CommentId, It.IsAny<CancellationToken>()), Times.Never);
        associations.Verify(item => item.DeleteObjectAssociationsAsync(CommentId, It.IsAny<CancellationToken>()), Times.Never);
        associations.Verify(item => item.DeleteAllAssociationAsync(CommentId, GraphAssociationType.LikedBy, It.IsAny<CancellationToken>()), Times.Exactly(2));
        associations.Verify(item => item.DeleteAllAssociationAsync(CommentId, GraphAssociationType.Mentioned, It.IsAny<CancellationToken>()), Times.Exactly(2));
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

    private static Mock<IAssociationService> EmptyAssociations()
    {
        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(
                It.IsAny<long>(),
                It.IsAny<short>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        return associations;
    }

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
