namespace SocialGraph.Api.Tests;

using Microsoft.EntityFrameworkCore;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;

/// <summary>
/// Deleting a user used to remove every item they had ever authored inside one transaction,
/// each cascading into association and orphan-media cleanup. For an account with thousands of
/// posts that ran for minutes holding locks on objects and associations — the two tables every
/// other request needs. The work is now committed in bounded batches.
/// </summary>
public sealed class DeleteUserBatchingTests
{
    private const long UserId = 9_000_000_000_000_010;

    [Fact]
    public async Task Every_authored_item_is_removed_even_beyond_one_batch()
    {
        // More than the batch size, so finishing proves the loop continues rather than
        // stopping after the first pass.
        await using var context = CreateContext(authoredItems: 250);
        var content = DeletingContentService(context);
        var service = CreateService(context, content.Object);

        var deleted = await service.DeleteUserAsync(UserId);

        Assert.True(deleted);
        Assert.Empty(context.AssociationsTb.Where(item => item.id1 == UserId));
        Assert.Equal(250, content.Invocations.Count);
    }

    [Fact]
    public async Task Deletion_stops_instead_of_looping_when_a_batch_removes_nothing()
    {
        await using var context = CreateContext(authoredItems: 10);
        var content = new Mock<IContentGraphService>();
        // Refuses every item, leaving the association rows in place. Re-reading would return
        // the same batch forever, so the loop has to give up rather than spin.
        content
            .Setup(item => item.DeleteContentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = CreateService(context, content.Object);

        var completed = await service.DeleteUserAsync(UserId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(completed);
        Assert.Equal(10, content.Invocations.Count);
    }

    [Fact]
    public async Task A_user_with_no_content_is_still_deleted()
    {
        await using var context = CreateContext(authoredItems: 0);
        var content = DeletingContentService(context);
        var service = CreateService(context, content.Object);

        Assert.True(await service.DeleteUserAsync(UserId));
        Assert.Empty(content.Invocations);
    }

    private static Mock<IContentGraphService> DeletingContentService(MyDbContext context)
    {
        var content = new Mock<IContentGraphService>();
        content
            .Setup(item => item.DeleteContentAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long contentId, CancellationToken _) =>
            {
                var edges = context.AssociationsTb.Where(item => item.id2 == contentId).ToList();
                context.AssociationsTb.RemoveRange(edges);
                context.SaveChanges();
                return true;
            });
        return content;
    }

    private static UserGraphService CreateService(MyDbContext context, IContentGraphService content)
    {
        var objects = new Mock<IObjectService>();
        objects
            .Setup(item => item.RetrieveObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SocialGraphObjectResult(UserId, GraphObjectType.User, "{}"));
        objects
            .Setup(item => item.DeleteObjectAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new UserGraphService(
            objects.Object,
            Mock.Of<IAssociationService>(),
            Mock.Of<IExternalServiceClient>(),
            context,
            content);
    }

    private static MyDbContext CreateContext(int authoredItems)
    {
        var context = new MyDbContext(new DbContextOptionsBuilder<MyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        for (var index = 1; index <= authoredItems; index++)
        {
            context.AssociationsTb.Add(new Associations
            {
                id1 = UserId,
                atype = GraphAssociationType.Authored,
                id2 = 5_000 + index,
                time = index
            });
        }
        context.SaveChanges();
        return context;
    }
}
