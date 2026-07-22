namespace SocialGraph.Api.Tests;

using SocialGraph.Api.Migrations;
using SocialGraph.Api.Service;

public sealed class AssociationContractTests
{
    [Fact]
    public void CanonicalAssociationCodes_AreStableAndContiguous()
    {
        var codes = new short[]
        {
            GraphAssociationType.Friend,
            GraphAssociationType.FriendRequest,
            GraphAssociationType.HaveFriendRequest,
            GraphAssociationType.Followed,
            GraphAssociationType.FollowedBy,
            GraphAssociationType.Blocked,
            GraphAssociationType.BlockedBy,
            GraphAssociationType.Liked,
            GraphAssociationType.LikedBy,
            GraphAssociationType.Authored,
            GraphAssociationType.AuthoredBy,
            GraphAssociationType.Published,
            GraphAssociationType.PublishedIn,
            GraphAssociationType.Member,
            GraphAssociationType.HaveMember,
            GraphAssociationType.Admin,
            GraphAssociationType.HaveAdmin,
            GraphAssociationType.GroupJoinRequest,
            GraphAssociationType.HaveGroupJoinRequest,
            GraphAssociationType.Watched,
            GraphAssociationType.WatchedBy,
            GraphAssociationType.HaveComment,
            GraphAssociationType.Comment,
            GraphAssociationType.Share,
            GraphAssociationType.SharedBy,
            GraphAssociationType.Tagged,
            GraphAssociationType.Mentioned,
            GraphAssociationType.Saved,
            GraphAssociationType.Contained,
            GraphAssociationType.Visited
        };

        Assert.Equal(Enumerable.Range(0, 30).Select(value => (short)value), codes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(5, 6)]
    [InlineData(6, 5)]
    [InlineData(7, 8)]
    [InlineData(8, 7)]
    [InlineData(9, 10)]
    [InlineData(10, 9)]
    [InlineData(11, 12)]
    [InlineData(12, 11)]
    [InlineData(13, 14)]
    [InlineData(14, 13)]
    [InlineData(15, 16)]
    [InlineData(16, 15)]
    [InlineData(17, 18)]
    [InlineData(18, 17)]
    [InlineData(19, 20)]
    [InlineData(20, 19)]
    [InlineData(21, 22)]
    [InlineData(22, 21)]
    [InlineData(23, 24)]
    [InlineData(24, 23)]
    public void InverseTypes_AreBidirectional(short associationType, short expectedInverse)
    {
        Assert.True(GraphAssociationRules.TryGetInverse(associationType, out var inverse));
        Assert.Equal(expectedInverse, inverse);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    public void OneWayTypes_DoNotCreateInventedInverse(short associationType)
    {
        Assert.False(GraphAssociationRules.TryGetInverse(associationType, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 7)]
    [InlineData(4, 8)]
    [InlineData(5, 9)]
    [InlineData(6, 10)]
    [InlineData(7, 21)]
    [InlineData(8, 23)]
    [InlineData(9, 11)]
    [InlineData(10, 12)]
    [InlineData(11, 25)]
    [InlineData(13, 13)]
    [InlineData(14, 14)]
    [InlineData(15, 15)]
    [InlineData(16, 16)]
    [InlineData(17, 19)]
    [InlineData(18, 20)]
    [InlineData(19, 27)]
    [InlineData(20, 28)]
    [InlineData(21, 26)]
    [InlineData(23, 5)]
    [InlineData(24, 6)]
    [InlineData(25, 29)]
    public void LegacyCodes_MapToCanonicalCodes(short legacyType, short canonicalType)
    {
        Assert.Equal(canonicalType, AssociationContractMigrationCommand.MapLegacyType(legacyType));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(22)]
    [InlineData(-1)]
    [InlineData(30)]
    public void UnsupportedLegacyCodes_AreDiscarded(short legacyType)
    {
        Assert.Null(AssociationContractMigrationCommand.MapLegacyType(legacyType));
    }

    [Fact]
    public void ObjectTypeRules_RejectWrongEndpointDirection()
    {
        Assert.True(GraphAssociationRules.IsValidForObjectTypes(
            GraphAssociationType.Authored,
            GraphObjectType.User,
            GraphObjectType.Reel));
        Assert.False(GraphAssociationRules.IsValidForObjectTypes(
            GraphAssociationType.Authored,
            GraphObjectType.Reel,
            GraphObjectType.User));
        Assert.True(GraphAssociationRules.IsValidForObjectTypes(
            GraphAssociationType.HaveComment,
            GraphObjectType.FeedPost,
            GraphObjectType.Comment));
        Assert.True(GraphAssociationRules.IsValidForObjectTypes(
            GraphAssociationType.Contained,
            GraphObjectType.Comment,
            GraphObjectType.Media));
        Assert.False(GraphAssociationRules.IsValidForObjectTypes(
            GraphAssociationType.Contained,
            GraphObjectType.User,
            GraphObjectType.Media));
    }
}
