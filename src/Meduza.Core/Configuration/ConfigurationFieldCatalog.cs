namespace Meduza.Core.Configuration;

public static class ConfigurationFieldCatalog
{
    private static readonly Dictionary<string, string> FieldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RecoveryEnabled"] = "DeviceRecoveryEnabled",
        ["DiscoveryEnabled"] = "AgentNetworkDiscoveryEnabled",
        ["P2PFilesEnabled"] = "P2PTransferEnabled",
        ["SupportEnabled"] = "RemoteSupportMeshCentralEnabled",
        ["MeshGroupPolicyProfile"] = "MeshCentralGroupPolicyProfile"
    };

    public static readonly string[] ManagedFields =
    [
        "DeviceRecoveryEnabled",
        "AgentNetworkDiscoveryEnabled",
        "P2PTransferEnabled",
        "RemoteSupportMeshCentralEnabled",
        "MeshCentralGroupPolicyProfile",
        "ChatAIEnabled",
        "KnowledgeBaseEnabled",
        "AppStorePolicy",
        "InventoryIntervalHours",
        "AutoUpdateSettingsJson",
        "AIIntegrationSettingsJson",
        "TicketAttachmentSettingsJson",
        "AgentHeartbeatIntervalSeconds"
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

        if (FieldAliases.TryGetValue(fieldName, out var mapped))
            return mapped;

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
