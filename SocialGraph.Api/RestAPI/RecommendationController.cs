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

    [HttpGet("post-candidate-ids")]
    public async Task<ActionResult<IReadOnlyList<long>>> GetPostCandidateIdsAsync(
        [FromQuery] long userId,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "userId must be positive." } });
        }

        return Ok(await _candidateService.GetPostCandidateIdsAsync(userId, limit, cancellationToken));
    }

    [HttpGet("reel-candidates")]
    public async Task<ActionResult<IReadOnlyList<CandidateItemResult>>> GetReelCandidatesAsync(
        [FromQuery] long userId,
        [FromQuery] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "userId must be positive." } });
        }

        return Ok(await _candidateService.GetReelCandidatesAsync(userId, limit, cancellationToken));
    }
}
