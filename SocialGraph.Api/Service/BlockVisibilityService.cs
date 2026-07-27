namespace SocialGraph.Api.Service;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Database;

/// <summary>
/// The single block-visibility kernel for user-to-user projections. A block in either
/// direction hides the other account; the viewer can never hide their own identity.
/// Keeping this query in one service prevents new list/projection paths from implementing
/// only one half of the reciprocal Blocked/BlockedBy relationship.
/// </summary>
public interface IBlockVisibilityService
{
    Task<IReadOnlySet<long>> GetBlockedUserIdsAsync(
        long viewerId,
        IEnumerable<long> candidateUserIds,
        CancellationToken cancellationToken = default);

    Task<bool> IsBlockedEitherDirectionAsync(
        long viewerId,
        long otherUserId,
        CancellationToken cancellationToken = default);
}

public sealed class BlockVisibilityService(MyDbContext dbContext) : IBlockVisibilityService
{
    public async Task<IReadOnlySet<long>> GetBlockedUserIdsAsync(
        long viewerId,
        IEnumerable<long> candidateUserIds,
        CancellationToken cancellationToken = default)
    {
        var candidates = candidateUserIds
            .Where(id => id > 0 && id != viewerId)
            .Distinct()
            .ToArray();
        if (viewerId <= 0 || candidates.Length == 0)
        {
            return new HashSet<long>();
        }

        return (await dbContext.AssociationsTb
                .AsNoTracking()
                .Where(edge =>
                    edge.id1 == viewerId &&
                    candidates.Contains(edge.id2) &&
                    (edge.atype == GraphAssociationType.Blocked ||
                     edge.atype == GraphAssociationType.BlockedBy))
                .Select(edge => edge.id2)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    public async Task<bool> IsBlockedEitherDirectionAsync(
        long viewerId,
        long otherUserId,
        CancellationToken cancellationToken = default)
    {
        if (viewerId <= 0 || otherUserId <= 0 || viewerId == otherUserId)
        {
            return false;
        }

        return await dbContext.AssociationsTb
            .AsNoTracking()
            .AnyAsync(edge =>
                edge.id1 == viewerId &&
                edge.id2 == otherUserId &&
                (edge.atype == GraphAssociationType.Blocked ||
                 edge.atype == GraphAssociationType.BlockedBy),
                cancellationToken);
    }
}
