namespace SocialGraph.Api.Tests;

using SocialGraph.Api.Service;

public sealed class MediaLifecycleReferenceTests
{
    private const long ParentId = 9_000_000_000_000_123;
    private const string Url = "/media/files/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg";

    [Fact]
    public void ProfileAndGroupSlotsHaveStableDistinctParentReferences()
    {
        Assert.Equal(
            $"socialgraph:user:{ParentId}:avatar",
            MediaLifecycleReferences.ForUserAvatar(ParentId, Url).ReferenceId);
        Assert.Equal(
            $"socialgraph:user:{ParentId}:background",
            MediaLifecycleReferences.ForUserBackground(ParentId, Url).ReferenceId);
        Assert.Equal(
            $"socialgraph:group:{ParentId}:avatar",
            MediaLifecycleReferences.ForGroupAvatar(ParentId, Url).ReferenceId);
        Assert.Equal(
            $"socialgraph:group:{ParentId}:background",
            MediaLifecycleReferences.ForGroupBackground(ParentId, Url).ReferenceId);
        Assert.Equal(
            $"socialgraph:media:{ParentId}",
            MediaLifecycleReferences.ForMedia(ParentId, Url).ReferenceId);
    }

    [Fact]
    public void ParentReferenceRejectsInvalidIdsAndBlankUrls()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaLifecycleReferences.ForMedia(0, Url));
        Assert.Throws<ArgumentException>(() =>
            MediaLifecycleReferences.ForUserAvatar(ParentId, " "));
    }
}
