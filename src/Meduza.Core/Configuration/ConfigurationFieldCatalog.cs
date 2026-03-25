namespace Meduza.Core.Configuration;

public static class ConfigurationFieldCatalog
{
    public static readonly string[] ManagedFields =
    [
        "RecoveryEnabled",
        "DiscoveryEnabled",
        "P2PFilesEnabled",
        "SupportEnabled",
        "MeshCentralGroupPolicyProfile",
        "ChatAIEnabled",
        "KnowledgeBaseEnabled",
        "AppStorePolicy",
        "InventoryIntervalHours",
        "AutoUpdateSettingsJson",
        "AIIntegrationSettingsJson",
        "TicketAttachmentSettingsJson",
        "AgentHeartbeatIntervalSeconds",
        "AgentOnlineGraceSeconds",
        "NatsAuthEnabled",
        "NatsAccountSeed",
        "NatsAgentJwtTtlMinutes",
        "NatsUserJwtTtlMinutes",
        "NatsUseScopedSubjects",
        "NatsIncludeLegacySubjects",
        "NatsXKeySeed",
        "NatsServerHostInternal",
        "NatsServerHostExternal",
        "NatsUseWssExternal"
    ];

    /// <summary>
    /// Campos de AIIntegrationSettings que são GLOBAIS — definidos exclusivamente no servidor
    /// e herdados por todos os clientes e sites sem possibilidade de sobrescrita.
    ///
    /// Quando AIIntegrationSettingsJson é salvo em ClientConfiguration ou SiteConfiguration,
    /// esses campos são removidos automaticamente pelo SanitizeAiOverrideJson.
    /// </summary>
    public static readonly HashSet<string> AiGlobalOnlyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiKey",
        "BaseUrl",
        "Provider",
        "EmbeddingModel",
        "EmbeddingEnabled",
        "EmbeddingArticlesEnabled",
        "MSPServers",
        "TimeoutMs",
        "RateLimitPerMinute",
        "TokenBudgetDaily",
        "CostControlEnabled",
    };

    public static string NormalizeFieldName(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return string.Empty;

        return fieldName;
    }

    public static HashSet<string> NormalizeFieldSet(IEnumerable<string> fields)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            var key = NormalizeFieldName(field);
            if (!string.IsNullOrWhiteSpace(key))
                normalized.Add(key);
        }

        return normalized;
    }
}
