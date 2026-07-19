namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Service;

public sealed record FriendIdsResponse(IReadOnlyList<long> UserIds);

[ApiController]
[Route("internal/users")]
public sealed class FriendIdsController(IUserGraphService userGraphService) : ControllerBase
{
    [HttpGet("{userId:long}/friend-ids")]
    public async Task<ActionResult<FriendIdsResponse>> GetFriendIdsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "userId must be positive." } });
        }

        var ids = await userGraphService.GetFriendIdsAsync(userId, cancellationToken);
        return Ok(new FriendIdsResponse(ids));
    }
}
