using Meduza.Core.DTOs;
using Meduza.Core.Interfaces.Auth;

namespace Meduza.Core.Interfaces;

public interface INatsCredentialsService
{
    Task<NatsCredentialsResponse> IssueForAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<NatsCredentialsResponse> IssueForUserAsync(Guid userId, UserScopeAccess scopeAccess, Guid? clientId, Guid? siteId, CancellationToken ct = default);
    Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForAgentAsync(string userPublicKey, Guid agentId, CancellationToken ct = default);
    Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForUserAsync(string userPublicKey, Guid userId, UserScopeAccess scopeAccess, CancellationToken ct = default);
}
