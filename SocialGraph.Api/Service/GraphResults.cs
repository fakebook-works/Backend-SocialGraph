namespace SocialGraph.Api.Service;

public sealed record SocialGraphObjectResult(long id, short otype, string data);

public sealed record AssociationEdgeResult(long id2, long time);

public sealed record AssociationPageResult(IReadOnlyList<AssociationEdgeResult> items, string? nextCursor);
