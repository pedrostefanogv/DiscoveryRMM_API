using Discovery.Core.DTOs;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve o orçamento de tokens de uma chamada de IA a partir da capacidade
/// real do modelo (catálogo), da configuração do tenant e de um teto opcional
/// por departamento.
/// </summary>
public interface IAiTokenBudgetResolver
{
    /// <summary>Resolve as configurações do site e devolve o orçamento model-aware.</summary>
    Task<AiTokenBudgetDto> ResolveForSiteAsync(
        Guid siteId, int requested, int? departmentCap = null, CancellationToken ct = default);

    /// <summary>
    /// Orçamento a partir de configurações JÁ resolvidas pelo chamador. Evita uma
    /// segunda resolução de configuração no mesmo request (performance) e é o
    /// caminho usado pelo AiChatService, que já tem settings e clientId em mãos.
    /// </summary>
    Task<AiTokenBudgetDto> ResolveAsync(
        Guid siteId, Guid? clientId, AIIntegrationSettings settings, int requested,
        int? departmentCap = null, CancellationToken ct = default);
}
