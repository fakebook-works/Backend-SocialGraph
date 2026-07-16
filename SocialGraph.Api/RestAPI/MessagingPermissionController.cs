namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Service;

[ApiController]
[Route("internal/messaging/permissions")]
public sealed class MessagingPermissionController : ControllerBase
{
    private readonly IMessagingPermissionService _permissions;

    public MessagingPermissionController(IMessagingPermissionService permissions)
    {
        _permissions = permissions;
    }

    [HttpPost("check")]
    public async Task<ActionResult<MessagingPermissionCheckResponse>> CheckAsync(
        [FromBody] MessagingPermissionCheckRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _permissions.CheckAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = new { code = "BAD_REQUEST", message = exception.Message } });
        }
    }
}

