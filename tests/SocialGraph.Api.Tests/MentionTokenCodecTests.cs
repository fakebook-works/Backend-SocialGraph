namespace SocialGraph.Api.Tests;

using SocialGraph.Api.Service;

public sealed class MentionTokenCodecTests
{
    [Fact]
    public void ExtractUserIds_ReturnsUniquePositiveLongIdsInContentOrder()
    {
        var result = MentionTokenCodec.ExtractUserIds(
            "A [[mention:9007199254740993124]] B [[mention:12]] C [[mention:9007199254740993124]]");

        Assert.Equal([9007199254740993124L, 12L], result);
    }

    [Fact]
    public void ExtractUserIds_IgnoresMalformedZeroNegativeAndOverflowTokens()
    {
        var result = MentionTokenCodec.ExtractUserIds(
            "[[mention:0]] [[mention:-1]] [[mention:9223372036854775808]] [[mention:abc]]");

        Assert.Empty(result);
    }
}
