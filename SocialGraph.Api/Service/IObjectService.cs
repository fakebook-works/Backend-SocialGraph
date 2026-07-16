namespace SocialGraph.Api.Service;

public interface IObjectService
{
    Task<SocialGraphObjectResult> AddObjectAsync(short otype, string dataJson, CancellationToken cancellationToken = default);
    Task<SocialGraphObjectResult?> UpdateObjectAsync(long id, short otype, string patchJson, CancellationToken cancellationToken = default);
    Task<SocialGraphObjectResult?> UpdateSystemObjectAsync(long id, short otype, string patchJson, CancellationToken cancellationToken = default);
    Task<bool> DeleteObjectAsync(long id, CancellationToken cancellationToken = default);
    Task<SocialGraphObjectResult?> RetrieveObjectAsync(long id, CancellationToken cancellationToken = default);
    Task InvalidateObjectCacheAsync(long id);
}
