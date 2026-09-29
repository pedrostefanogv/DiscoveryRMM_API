namespace Discovery.Core.Helpers;

/// <summary>
/// Chaves de cache compartilhadas do processo de auto-labeling.
/// Vivem em <c>Discovery.Core</c> para que produtor (AgentAutoLabelingService)
/// e consumidor/invalidador (LabelService) nao possam divergir — antes a chave
/// era um <c>private const</c> no servico, o que permitia que os writes a
/// esquecessem de invalidar.
/// </summary>
public static class AgentLabelingCacheKeys
{
    /// <summary>Lista serializada das regras habilitadas (TTL curto, apenas rede de seguranca).</summary>
    public const string EnabledRules = "label-rules:enabled";

    /// <summary>TTL em segundos do cache de regras habilitadas.</summary>
    public const int EnabledRulesTtlSeconds = 300;

    /// <summary>
    /// Marca d'agua da reconciliacao incremental: instante do inicio da ultima passagem
    /// concluida. A proxima passagem avalia apenas agentes alterados depois disso.
    /// </summary>
    public const string ReconciliationWatermark = "label-reconciliation:watermark";

    /// <summary>TTL do watermark (rede de seguranca; ele e reescrito a cada passagem).</summary>
    public const int ReconciliationWatermarkTtlSeconds = 7 * 24 * 3600;

    /// <summary>Prefixo do progresso dos jobs de reprocessamento (compartilhado entre replicas).</summary>
    public const string ReprocessProgressPrefix = "label-reprocess:progress:";

    /// <summary>TTL do progresso de um job (tempo suficiente para o usuario acompanhar).</summary>
    public const int ReprocessProgressTtlSeconds = 3600;
}
