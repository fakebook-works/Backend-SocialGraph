namespace SocialGraph.Api.Tests;

using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

public sealed class RecommendationHydrationTests
{
    [Fact]
    public async Task RecommendationItems_BatchHydratePostsForAuthenticatedViewer()
    {
        IReadOnlyList<long>? capturedIds = null;
        var content = new Mock<IContentGraphService>();
        content
            .Setup(item => item.GetPostDetailsAsync(
                42,
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()))
            .Callback<long, IReadOnlyList<long>, CancellationToken>((_, ids, _) => capturedIds = ids.ToArray())
            .ReturnsAsync((long _, IReadOnlyList<long> ids, CancellationToken _) =>
                ids.Select(id => (IHomePostResult)new FeedPostDetailResult(
                        id,
                        GraphObjectType.FeedPost,
                        $"post-{id}",
                        0,
                        "2026-07-12T00:00:00Z",
                        new PostAuthorResult(7, "Author", "", false, false),
                        Array.Empty<MediaResult>()))
                    .ToArray());
        var trustedCaller = new Mock<ITrustedCallerAccessor>();
        trustedCaller.Setup(item => item.RequireUserId()).Returns(42);
        var services = new ServiceCollection();
        services.AddSingleton(content.Object);
        services.AddSingleton(trustedCaller.Object);
        services.AddDataLoader<HomePostByIdDataLoader>();
        services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddType<RecommendationItemResult>()
            .AddTypeExtension<RecommendationItemResolvers>()
            .AddType<FeedPostDetailResult>()
            .AddType<GroupPostDetailResult>()
            .AddType<NormalStoryResult>()
            .AddType<FeedPostShareStoryResult>()
            .AddType<ReelShareStoryResult>()
            .AddType<FeedPostSharedSourceResult>()
            .AddType<ReelSharedSourceResult>();
        await using var provider = services.BuildServiceProvider();
        var executor = await provider
            .GetRequiredService<IRequestExecutorProvider>()
            .GetExecutorAsync();

        var result = await executor.ExecuteAsync(
            """
            query {
              first: recommendationItem(postId: "1001") {
                postId
                post {
                  ... on FeedPostDetail { id content }
                }
              }
              second: recommendationItem(postId: "1002") {
                postId
                post {
                  ... on FeedPostDetail { id content }
                }
              }
            }
            """);

        Assert.Empty(result.ExpectOperationResult().Errors);
        Assert.Equal(new long[] { 1001, 1002 }, capturedIds?.OrderBy(id => id));
        content.Verify(
            item => item.GetPostDetailsAsync(
                42,
                It.IsAny<IReadOnlyList<long>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
