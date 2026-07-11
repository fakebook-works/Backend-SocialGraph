namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Service;

public sealed record SetUserVerifyRequest(DateTimeOffset? ExpiresAt);

[ApiController]
[Route("internal/users")]
public sealed class PaymentController : ControllerBase
{
    private readonly IUserGraphService _userGraphService;

    public PaymentController(IUserGraphService userGraphService)
    {
        _userGraphService = userGraphService;
    }

    [HttpPut("{userId:long}/verify")]
    public async Task<ActionResult<UserProfileResult>> SetUserVerifyAsync(
        long userId,
        [FromBody] SetUserVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _userGraphService.SetUserVerifyAsync(userId, request.ExpiresAt, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
