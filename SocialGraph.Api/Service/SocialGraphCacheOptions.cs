namespace SocialGraph.Api.Service;

public sealed class SocialGraphCacheOptions
{
    public const string SectionName = "Cache";
    public string Mode { get; set; } = "auto";

    public bool Enabled => !string.Equals(Mode, "off", StringComparison.OrdinalIgnoreCase);
}
