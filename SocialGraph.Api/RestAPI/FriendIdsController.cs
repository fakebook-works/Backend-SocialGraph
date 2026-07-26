namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Service;

public sealed record FriendIdsResponse(IReadOnlyList<long> UserIds);
public sealed record ProfileConnectionIdsResponse(IReadOnlyList<long> UserIds);

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

    [HttpGet("{userId:long}/profile-connection-ids")]
    public async Task<ActionResult<ProfileConnectionIdsResponse>> GetProfileConnectionIdsAsync(
        long userId,
        [FromQuery] short associationType,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "userId must be positive." } });
        }
        if (associationType is not (GraphAssociationType.Friend or GraphAssociationType.Followed or GraphAssociationType.FollowedBy))
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = "associationType must be friend, following or follower." } });
        }

        var ids = await userGraphService.GetProfileConnectionIdsAsync(userId, associationType, cancellationToken);
        return Ok(new ProfileConnectionIdsResponse(ids));
    }
}
