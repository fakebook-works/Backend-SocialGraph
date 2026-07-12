namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Service;

[ApiController]
[Route("internal/stories")]
public sealed class StoryMaintenanceController(IContentGraphService contentGraphService) : ControllerBase
{
    [HttpDelete("expired")]
    public async Task<ActionResult<StoryCleanupPayload>> CleanupExpiredStoriesAsync(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var deleted = await contentGraphService.CleanupExpiredStoriesAsync(limit, cancellationToken);
        return Ok(new StoryCleanupPayload(deleted));
    }
}
