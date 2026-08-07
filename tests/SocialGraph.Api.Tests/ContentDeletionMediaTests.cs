namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

public sealed class ContentDeletionMediaTests
{
    [Theory]
    [InlineData(GraphObjectType.FeedPost)]
    [InlineData(GraphObjectType.GroupPost)]
    [InlineData(GraphObjectType.Reel)]
    public async Task DeleteContent_DetachesMediaFromEveryDescendantComment(short contentType)
    {
        await using var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        const long contentId = 100;
        long[] commentIds = [200, 201, 202];
        var mediaIds = Enumerable.Range(300, 104).Select(value => (long)value).ToArray();
        context.ObjectsTb.Add(new Objects { id = contentId, otype = contentType, data = "{}" });
        context.ObjectsTb.AddRange(commentIds.Select(id =>
            new Objects { id = id, otype = GraphObjectType.Comment, data = "{}" }));
        context.ObjectsTb.AddRange(mediaIds.Select(id =>
            new Objects
            {
                id = id,
                otype = GraphObjectType.Media,
                data = GraphJson.MediaJson(GraphMediaType.Photo, $"/media/files/{id}.avif")
            }));
        context.AssociationsTb.AddRange(
            Edge(contentId, GraphAssociationType.HaveComment, commentIds[0]),
            Edge(commentIds[0], GraphAssociationType.HaveComment, commentIds[1]),
            Edge(commentIds[1], GraphAssociationType.HaveComment, commentIds[2]),
            Edge(commentIds[0], GraphAssociationType.Contained, mediaIds[101]),
            Edge(commentIds[1], GraphAssociationType.Contained, mediaIds[102]),
            Edge(commentIds[2], GraphAssociationType.Contained, mediaIds[103]));
        context.AssociationsTb.AddRange(mediaIds.Take(101).Select(mediaId =>
            Edge(contentId, GraphAssociationType.Contained, mediaId)));
        await context.SaveChangesAsync();

        var objects = new Mock<IObjectService>(MockBehavior.Loose);
        objects.Setup(item => item.RetrieveObjectAsync(contentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(contentId, contentType, "{}"));
        objects.Setup(item => item.DeleteObjectAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, CancellationToken _) =>
            {
                var row = context.ObjectsTb.SingleOrDefault(item => item.id == id);
                if (row is null)
                {
                    return false;
                }

                context.ObjectsTb.Remove(row);
                context.SaveChanges();
                return true;
            });

        var associations = new Mock<IAssociationService>(MockBehavior.Loose);
        associations.Setup(item => item.RetrieveAssociationAsync(
                It.IsAny<long>(),
                GraphAssociationType.Contained,
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssociationPageResult(Array.Empty<AssociationEdgeResult>(), null));
        associations.Setup(item => item.DeleteObjectAssociationsAsync(
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id, CancellationToken _) =>
            {
                var rows = context.AssociationsTb
                    .Where(edge => edge.id1 == id || edge.id2 == id)
                    .ToArray();
                context.AssociationsTb.RemoveRange(rows);
                context.SaveChanges();
                return rows.Length;
            });
        associations.Setup(item => item.DeleteOneAssociationAsync(
                It.IsAny<long>(),
                It.IsAny<short>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((long id1, short type, long id2, CancellationToken _) =>
            {
                var row = context.AssociationsTb.SingleOrDefault(edge =>
                    edge.id1 == id1 && edge.atype == type && edge.id2 == id2);
                if (row is null)
                {
                    return false;
                }

                context.AssociationsTb.Remove(row);
                context.SaveChanges();
                return true;
            });

        var detached = new List<MediaLifecycleReference>();
        var external = new Mock<IExternalServiceClient>(MockBehavior.Loose);
        external.Setup(item => item.DeleteMediaAsync(
                It.IsAny<IReadOnlyList<MediaLifecycleReference>>(),
                null,
                It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<MediaLifecycleReference> references, long? _, CancellationToken _) =>
                detached.AddRange(references))
            .Returns(Task.CompletedTask);
        var service = new ContentGraphService(
            context,
            objects.Object,
            associations.Object,
            external.Object);

        Assert.True(await service.DeleteContentAsync(contentId));

        Assert.Equal(mediaIds.Order(), detached.Select(reference =>
            long.Parse(reference.ReferenceId.Split(':')[2])).Order());
        Assert.Empty(context.ObjectsTb.Where(item => mediaIds.Contains(item.id)));
        Assert.DoesNotContain(context.AssociationsTb, item =>
            item.atype == GraphAssociationType.Contained && mediaIds.Contains(item.id2));
        associations.Verify(item => item.RetrieveAssociationAsync(
            It.IsAny<long>(),
            GraphAssociationType.Contained,
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Associations Edge(long id1, short type, long id2) => new()
    {
        id1 = id1,
        atype = type,
        id2 = id2,
        time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
}
