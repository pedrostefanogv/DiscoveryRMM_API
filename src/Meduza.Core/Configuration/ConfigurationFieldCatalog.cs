namespace Meduza.Core.Configuration;

public static class ConfigurationFieldCatalog
{
    private static readonly Dictionary<string, string> FieldAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RecoveryEnabled"] = "DeviceRecoveryEnabled",
        ["DiscoveryEnabled"] = "AgentNetworkDiscoveryEnabled",
        ["P2PFilesEnabled"] = "P2PTransferEnabled",
        ["SupportEnabled"] = "RemoteSupportMeshCentralEnabled"
    };

    public static readonly string[] ManagedFields =
    [
        "DeviceRecoveryEnabled",
        "AgentNetworkDiscoveryEnabled",
        "P2PTransferEnabled",
        "RemoteSupportMeshCentralEnabled",
        "ChatAIEnabled",
        "KnowledgeBaseEnabled",
        "AppStorePolicy",
        "InventoryIntervalHours",
        "AutoUpdateSettingsJson",
        "AIIntegrationSettingsJson",
        "TokenExpirationDays",
        "MaxTokensPerAgent",
        "AgentHeartbeatIntervalSeconds",
        "AgentOfflineThresholdSeconds"
    ];

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
