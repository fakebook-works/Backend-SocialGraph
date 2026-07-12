namespace SocialGraph.Api.Service;

using SocialGraph.Api.Contracts;

public interface ICandidateService
{
    Task<IReadOnlyList<CandidateItemResult>> GetPostCandidatesAsync(long userId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetPostCandidateIdsAsync(long userId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CandidateItemResult>> GetReelCandidatesAsync(long userId, int limit, CancellationToken cancellationToken = default);
}
