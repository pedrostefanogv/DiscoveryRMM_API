using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services.Ai;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve o orçamento de tokens de uma chamada de IA a partir da capacidade
/// real do modelo (catálogo) + configuração do tenant + teto do produto.
///
/// O catálogo tem cache próprio de 60 min; aqui há um cache curto por modelo
/// para que a triagem (uma execução por chamado) não consulte o catálogo a cada
/// chamado.
/// </summary>
public class AiTokenBudgetResolver(
    IConfigurationResolver configurationResolver,
    IAiModelCatalogService modelCatalog,
    IMemoryCache cache,
    ILogger<AiTokenBudgetResolver> logger) : IAiTokenBudgetResolver
{
    private static readonly TimeSpan ModelCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// TTL curto para "modelo não encontrado": se o gestor corrigir o id, o efeito
    /// aparece em minutos em vez de esperar o TTL cheio.
    /// </summary>
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Orçamento para um site, aplicando um teto opcional do departamento.</summary>
    public async Task<AiTokenBudgetDto> ResolveForSiteAsync(
        Guid siteId, int requested, int? departmentCap = null, CancellationToken ct = default)
    {
        AIIntegrationSettings settings;
        Guid? clientId = null;
        try
        {
            var resolved = await configurationResolver.ResolveForSiteAsync(siteId);
            settings = resolved.AIIntegration ?? new AIIntegrationSettings();
            clientId = resolved.ClientId;
        }
        catch (Exception ex)
        {
            // Site inexistente: segue com o teto conservador em vez de falhar.
            logger.LogDebug(ex, "Não foi possível resolver as configurações de IA para o orçamento de tokens.");
            return AiTokenLimits.BuildBudget(null, requested, departmentCap, null);
        }

        return await ResolveAsync(siteId, clientId, settings, requested, departmentCap, ct);
    }

    public async Task<AiTokenBudgetDto> ResolveAsync(
        Guid siteId, Guid? clientId, AIIntegrationSettings settings, int requested,
        int? departmentCap = null, CancellationToken ct = default)
    {
        // Teto do tenant só vale dentro da faixa válida; fora dela (inclusive 0 =
        // AUTOMÁTICO) NÃO há pedido do tenant e o teto do produto/modelo assume,
        // em vez de rebaixar silenciosamente para um default.
        var configuredCap = settings.MaxTokensPerRequest >= AiChatConstants.MinExplicitMaxTokens
            && settings.MaxTokensPerRequest <= AiTokenLimits.MaxOutputTokensCeiling
                ? settings.MaxTokensPerRequest
                : (int?)null;

        // Duas grandezas DIFERENTES sendo combinadas:
        //   - requested     = pedido DESTA chamada; 0 = AUTOMÁTICO (não restringe);
        //   - configuredCap = teto EXPLÍCITO do administrador na configuração do
        //     tenant. Continua valendo mesmo no automático: quem pediu "use a
        //     capacidade do modelo" não autorizou ignorar o teto configurado.
        // Por isso o 0 do pedido vira "sem restrição" (int.MaxValue) ANTES do min,
        // e só permanece 0 (automático de ponta a ponta) quando o tenant também
        // não definiu teto nenhum.
        int effectiveRequested;
        if (requested > 0)
            effectiveRequested = configuredCap.HasValue ? Math.Min(requested, configuredCap.Value) : requested;
        else
            effectiveRequested = configuredCap ?? 0;

        var model = await TryGetModelAsync(clientId, siteId, settings.ChatModel, ct);
        return AiTokenLimits.BuildBudget(model, effectiveRequested, departmentCap, settings.ChatModel);
    }

    private async Task<AiModelInfo?> TryGetModelAsync(
        Guid? clientId, Guid siteId, string? modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var key = "ai-token-budget:model:" + modelId.Trim().ToLowerInvariant();
        if (cache.TryGetValue(key, out ModelCacheEntry? cached) && cached is not null)
            return cached.Model;

        AiModelInfo? model = null;
        try
        {
            model = await modelCatalog.GetModelAsync(clientId, siteId, modelId, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Catálogo de modelos indisponível para o orçamento de tokens; usando fallback.");
        }

        // Cacheia inclusive quando não encontrou: evita repetir a consulta em
        // cada chamado para um id inválido/retirado.
        cache.Set(key, new ModelCacheEntry(model),
            model is null ? NegativeCacheTtl : ModelCacheTtl);
        return model;
    }

    private sealed record ModelCacheEntry(AiModelInfo? Model);
}
