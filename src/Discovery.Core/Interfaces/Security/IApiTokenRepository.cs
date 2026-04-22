using Discovery.Core.Entities.Security;

namespace Discovery.Core.Interfaces.Security;

public interface IApiTokenRepository
{
    Task<ApiToken?> GetByIdAsync(Guid id);
    Task<ApiToken?> GetByTokenIdPublicAsync(string tokenIdPublic);
    Task<IEnumerable<ApiToken>> GetByUserIdAsync(Guid userId);
    Task<ApiToken> CreateAsync(ApiToken token);
    Task<bool> RevokeAsync(Guid id, Guid userId);
    Task UpdateLastUsedAsync(Guid id);
}
