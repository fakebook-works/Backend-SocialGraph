namespace SocialGraph.Api.Service;

public sealed class SocialGraphCacheOptions
{
    public const string SectionName = "Cache";
    public string Mode { get; set; } = "auto";

    public bool Enabled => !string.Equals(Mode, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How long a cached entry lives before Redis reclaims it.
    /// </summary>
    /// <remarks>
    /// Nothing expired before: association buckets, their markers and cached objects were all
    /// written without a lifetime, so Redis grew for as long as the process ran, and any entry
    /// left behind by a rolled-back write stayed authoritative indefinitely because the read
    /// paths prefer the cache. A lifetime bounds both.
    /// </remarks>
    public int EntryTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Largest association bucket that will be cached.
    /// </summary>
    /// <remarks>
    /// Hydration used to read an entire bucket and push all of it into a sorted set with no
    /// limit, so reading the first page for an account with a million followers pulled a
    /// million rows across the network. Buckets above this size skip the cache and are served
    /// from PostgreSQL instead, which is bounded by the page size. Caching them partially is
    /// not an option: the read path derives the next cursor from the cached length, so a
    /// truncated bucket would silently end pagination early.
    /// </remarks>
    public int MaxCachedAssociationEntries { get; set; } = 5_000;

    /// <summary>
    /// How long a bucket stays marked as too large before its size is measured again.
    /// </summary>
    public int BypassMarkerTtlMinutes { get; set; } = 10;
}
