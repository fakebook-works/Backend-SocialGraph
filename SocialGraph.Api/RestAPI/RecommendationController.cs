namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Service;

[ApiController]
[Route("internal/recommendation")]
public sealed class RecommendationController : ControllerBase
{
    private readonly ICandidateService _candidateService;

    public RecommendationController(ICandidateService candidateService)
    {
        _candidateService = candidateService;
    }

    [HttpGet("post-candidates")]
    public Task<IReadOnlyList<CandidateItemResult>> GetPostCandidatesAsync(
        [FromQuery] long userId,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _candidateService.GetPostCandidatesAsync(userId, limit, cancellationToken);
    }

    [HttpGet("reel-candidates")]
    public Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(
        [FromQuery] long userId,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _candidateService.GetReelCandidatesAsync(userId, limit, cancellationToken);
    }
}
