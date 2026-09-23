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

    /// <summary>Prefixo de progresso dos jobs de reprocessamento.</summary>
    public const string ReprocessProgressPrefix = "label-reprocess:progress:";
}
