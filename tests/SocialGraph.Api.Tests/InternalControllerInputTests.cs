namespace SocialGraph.Api.Tests;

using Microsoft.AspNetCore.Mvc;
using Moq;
using SocialGraph.Api.RestAPI;
using SocialGraph.Api.Service;

public sealed class InternalControllerInputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RecommendationEndpoints_RejectNonPositiveUserIds(long userId)
    {
        var controller = new RecommendationController(Mock.Of<ICandidateService>());

        var posts = await controller.GetPostCandidateIdsAsync(userId);
        var reels = await controller.GetReelCandidatesAsync(userId);

        Assert.IsType<BadRequestObjectResult>(posts.Result);
        Assert.IsType<BadRequestObjectResult>(reels.Result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PaymentEndpoint_RejectsNonPositiveUserIds(long userId)
    {
        var controller = new PaymentController(Mock.Of<IUserGraphService>());

        var result = await controller.SetUserVerifyAsync(
            userId,
            new SetUserVerifyRequest(null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
