namespace SocialGraph.Api.Database;

public sealed class IntegrationOutboxMessage
{
    public Guid id { get; set; }
    public string event_type { get; set; } = string.Empty;
    public long? aggregate_id { get; set; }
    public string idempotency_key { get; set; } = string.Empty;
    public string payload { get; set; } = "{}";
    public DateTimeOffset created_at { get; set; }
    public DateTimeOffset available_at { get; set; }
    public int attempts { get; set; }
    public int max_attempts { get; set; }
    public short status { get; set; }
    public DateTimeOffset? locked_at { get; set; }
    public string? locked_by { get; set; }
    public string? last_error { get; set; }
    public DateTimeOffset? completed_at { get; set; }
}

