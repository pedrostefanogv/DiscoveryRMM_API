using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Service responsável pela transferência de agentes entre sites/clientes.
/// Lida com validação de permissões cross-scope, atualização de ACLs no MeshCentral
/// e notificações em tempo real.
/// </summary>
public interface IAgentTransferService
{
    /// <summary>
    /// Transfere um agente para outro site.
    /// Se o site destino pertencer a outro cliente, a transferência é cross-client
    /// e requer permissão em ambos os escopos, além de atualização de ACL no MeshCentral.
    /// </summary>
    /// <param name="agentId">ID do agente a ser transferido.</param>
    /// <param name="targetSiteId">ID do site de destino.</param>
    /// <param name="userId">ID do usuário solicitante.</param>
    /// <param name="reason">Motivo opcional da transferência.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Resultado da transferência com o agente atualizado e metadados.</returns>
    Task<AgentTransferResult> TransferAsync(
        Guid agentId,
        Guid targetSiteId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfere múltiplos agentes para um site de destino.
    /// A operação é atômica por agente: falhas em um não afetam os demais.
    /// </summary>
    Task<BulkAgentTransferResult> BulkTransferAsync(
        IReadOnlyList<Guid> agentIds,
        Guid targetSiteId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pré-valida se uma transferência pode ser realizada sem executá-la.
    /// Útil para UIs que querem verificar viabilidade antes de confirmar.
    /// </summary>
    Task<AgentTransferValidation> ValidateAsync(
        Guid agentId,
        Guid targetSiteId,
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resultado da transferência de um agente.
/// </summary>
public sealed record AgentTransferResult
{
    /// <summary>Agente após a transferência (com SiteId atualizado).</summary>
    public required Agent Agent { get; init; }

    /// <summary>ID do site de origem.</summary>
    public required Guid PreviousSiteId { get; init; }

    /// <summary>ID do cliente de origem (pode ser igual ao destino se mesma organização).</summary>
    public required Guid PreviousClientId { get; init; }

    /// <summary>ID do cliente de destino.</summary>
    public required Guid TargetClientId { get; init; }

    /// <summary>Indica se a transferência foi entre clientes diferentes.</summary>
    public bool IsCrossClient => PreviousClientId != TargetClientId;

    /// <summary>Indica se a ACL do MeshCentral foi atualizada.</summary>
    public bool MeshCentralAclUpdated { get; init; }

    /// <summary>Motivo registrado para a transferência.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Resultado da transferência em lote.
/// </summary>
public sealed record BulkAgentTransferResult
{
    /// <summary>Resultados individuais de cada agente.</summary>
    public IReadOnlyList<AgentTransferResult> Results { get; init; } = [];

    /// <summary>Agentes que falharam na transferência com o erro correspondente.</summary>
    public IReadOnlyList<AgentTransferError> Errors { get; init; } = [];

    /// <summary>Quantidade de agentes transferidos com sucesso.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Quantidade de agentes com falha.</summary>
    public int ErrorCount { get; init; }
}

/// <summary>
/// Erro ocorrido durante transferência em lote.
/// </summary>
public sealed record AgentTransferError
{
    /// <summary>ID do agente que falhou.</summary>
    public required Guid AgentId { get; init; }

    /// <summary>Mensagem de erro.</summary>
    public required string Error { get; init; }
}

/// <summary>
/// Resultado da pré-validação de uma transferência.
/// </summary>
public sealed record AgentTransferValidation
{
    /// <summary>Se a transferência é válida.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Mensagens de validação (erros ou avisos).</summary>
    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>Indica se a transferência seria cross-client.</summary>
    public bool IsCrossClient { get; init; }

    /// <summary>Nome do site de origem (para exibição).</summary>
    public string? PreviousSiteName { get; init; }

    /// <summary>Nome do site de destino (para exibição).</summary>
    public string? TargetSiteName { get; init; }

    /// <summary>Nome do cliente de origem.</summary>
    public string? PreviousClientName { get; init; }

    /// <summary>Nome do cliente de destino.</summary>
    public string? TargetClientName { get; init; }
}
