namespace SocialGraph.Api.Service;

public interface IAssociationService
{
    Task<bool> AddAssociationAsync(long id1, short atype, long id2, CancellationToken cancellationToken = default);
    Task<bool> ApplyMutationsAsync(IReadOnlyCollection<AssociationMutation> mutations, CancellationToken cancellationToken = default);
    Task<bool> HasAssociationAsync(long id1, short atype, long id2, CancellationToken cancellationToken = default);
    Task<bool> DeleteOneAssociationAsync(long id1, short atype, long id2, CancellationToken cancellationToken = default);
    Task<bool> DeleteAllAssociationAsync(long id1, short atype, CancellationToken cancellationToken = default);
    Task<int> DeleteObjectAssociationsAsync(long objectId, CancellationToken cancellationToken = default);
    Task<long> CountAssociationAsync(long id1, short atype, CancellationToken cancellationToken = default);
    Task<AssociationPageResult> RetrieveAssociationAsync(long id1, short atype, string? cursor, int limit, CancellationToken cancellationToken = default);
}
