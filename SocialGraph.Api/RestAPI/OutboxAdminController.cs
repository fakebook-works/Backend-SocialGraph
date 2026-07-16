namespace SocialGraph.Api.RestAPI;

using Microsoft.AspNetCore.Mvc;
using SocialGraph.Api.Infrastructure.Outbox;

[ApiController]
[Route("internal/outbox")]
public sealed class OutboxAdminController : ControllerBase
{
    private readonly IIntegrationOutboxStore _store;

    public OutboxAdminController(IIntegrationOutboxStore store)
    {
        _store = store;
    }

    [HttpGet("dead-letters")]
    public async Task<ActionResult<IReadOnlyList<OutboxDeadLetterResult>>> ListDeadLettersAsync(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var messages = await _store.ListDeadLettersAsync(limit, cancellationToken);
        return Ok(messages.Select(message => new OutboxDeadLetterResult(
            message.id,
            message.event_type,
            message.aggregate_id,
            message.created_at,
            message.attempts,
            message.max_attempts,
            message.last_error)).ToArray());
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _store.RequeueDeadLetterAsync(id, cancellationToken)
            ? Accepted(new { id, status = "pending" })
            : NotFound();
    }
}

public sealed record OutboxDeadLetterResult(
    Guid Id,
    string EventType,
    long? AggregateId,
    DateTimeOffset CreatedAt,
    int Attempts,
    int MaxAttempts,
    string? LastError);
