using Discovery.Core.DTOs;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Core.Interfaces;

public interface INatsCredentialsService
{
    Task<NatsCredentialsResponse> IssueForAgentAsync(Guid agentId, CancellationToken ct = default);
    Task<NatsCredentialsResponse> IssueForUserAsync(Guid userId, UserScopeAccess scopeAccess, Guid? clientId, Guid? siteId, CancellationToken ct = default, UserScopeAccess? remoteDebugScopeAccess = null);
    Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForAgentAsync(string userPublicKey, Guid agentId, CancellationToken ct = default);
    Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForUserAsync(string userPublicKey, Guid userId, UserScopeAccess scopeAccess, CancellationToken ct = default, UserScopeAccess? remoteDebugScopeAccess = null);

    /// <summary>
    /// Emite credenciais NATS scoped para uma sessão de acesso remoto.
    /// Gera um par de chaves NKey + JWT assinado com a account key, com permissões
    /// pub/sub limitadas aos subjects da sessão.
    /// </summary>
    Task<(string Jwt, string NkeySeed, DateTime ExpiresAtUtc)> IssueSessionCredentialsAsync(
        string[] publishSubjects,
        string[] subscribeSubjects,
        int ttlMinutes,
        string traceLabel,
        CancellationToken ct = default);

    /// <summary>
    /// Reemite um JWT de sessão remota para uma userNkey específica (auth callout).
    /// Usado quando o viewer conecta com o JWT original mas o NATS exige que o
    /// subject da resposta bata com o userNkey efêmero do WebSocket.
    /// </summary>
    Task<(string Jwt, DateTime ExpiresAtUtc)> IssueSessionJwtForPublicKeyAsync(
        string userPublicKey,
        string[] publishSubjects,
        string[] subscribeSubjects,
        int ttlMinutes,
        string traceLabel,
        CancellationToken ct = default);
}
