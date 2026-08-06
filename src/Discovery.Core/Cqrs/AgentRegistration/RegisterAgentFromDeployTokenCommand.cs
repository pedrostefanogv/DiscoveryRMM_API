using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentRegistration;

/// <summary>
/// Comando para auto-registro de um agent usando deploy token.
/// Executado durante o bootstrap do instalador — o agent chama o endpoint público
/// com o deploy token recebido no instalador e recebe de volta as credenciais
/// (token mdz_ + agentId + clientId + siteId).
/// </summary>
public sealed record RegisterAgentFromDeployTokenCommand(
    string DeployToken,
    string Hostname,
    string? MacAddress,
    string? Notes,
    string? TpmEkHash = null,
    string? SmbiosUuid = null
) : ICommand<Result<AgentRegistrationResult>>;

/// <summary>
/// Resultado do registro do agent via deploy token.
/// </summary>
public sealed record AgentRegistrationResult(
    string Token,
    Guid AgentId,
    Guid ClientId,
    Guid SiteId,
    bool Recovered = false
);
