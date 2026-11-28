using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id);

    /// <summary>Busca varios agentes em uma unica query (evita N+1 no reprocessamento de labels).</summary>
    Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<IEnumerable<Agent>> GetAllAsync();
    Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId);
    Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId);
    Task<Agent> CreateAsync(Agent agent);
    Task UpdateAsync(Agent agent);
    Task UpdateStatusAsync(Guid id, Enums.AgentStatus status, string? ipAddress);

    /// <summary>
    /// Grava apenas o resultado do policy-sync de automação (fingerprint +
    /// quando), sem reescrever a linha do agent. Chamado pelo sync do agent.
    /// </summary>
    Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default);
    Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default);

    /// <summary>
    /// Agentes online para fins de REENTREGA de comandos, INCLUINDO os que estão
    /// na lixeira (soft-deleted). É o oposto deliberado de
    /// <see cref="GetOnlineAsync"/>: um agente na lixeira continua instalado e
    /// rodando na máquina até o uninstall confirmar, e precisa receber o
    /// DecommissionAgent que ficou Pending. O filtro por tipo de comando fica a
    /// cargo da guarda da reentrega (um agente na lixeira só aceita
    /// DecommissionAgent).
    /// </summary>
    Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default);

    /// <summary>
    /// Busca o agente IGNORANDO o filtro global de soft delete. Usado no
    /// caminho de descomissionamento: um agente na lixeira continua instalado
    /// e precisa continuar autenticando até o uninstall confirmar.
    /// </summary>
    Task<Agent?> GetByIdForDecommissionAsync(Guid id);
    Task ApproveZeroTouchAsync(Guid agentId);
    Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId);
    Task TransferSiteAsync(Guid agentId, Guid newSiteId);
    Task DeleteAsync(Guid id);

    /// <summary>
    /// Busca agents (incluindo soft-deleted) que possuam o fingerprint informado e pertençam ao cliente.
    /// Usado pela Recuperação de Dispositivos para reutilizar um agent já registrado.
    /// </summary>
    Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default);

    /// <summary>
    /// Registra o serial e expiração do último certificado mTLS emitido para o agent.
    /// </summary>
    Task UpdateMtlsCertAsync(Guid id, string serialNumber, DateTime expiresAtUtc, CancellationToken ct = default)
        => Task.CompletedTask;
}
