namespace Discovery.Core.Interfaces;

/// <summary>
/// Exclusão definitiva (física) de um agente. Remove, em uma única transação,
/// todas as linhas que referenciam o agente e preserva histórico (chamados/logs)
/// desvinculando <c>agent_id</c> em vez de apagar.
/// </summary>
public interface IAgentPurgeService
{
    Task PurgeAsync(Guid agentId, CancellationToken ct = default);
}
