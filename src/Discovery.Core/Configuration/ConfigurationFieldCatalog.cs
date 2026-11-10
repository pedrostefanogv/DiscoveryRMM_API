namespace Discovery.Core.Configuration;

public static class ConfigurationFieldCatalog
{
    public static readonly string[] ManagedFields =
    [
        "RecoveryEnabled",
        "DiscoveryEnabled",
        "P2PFilesEnabled",
        "CloudBootstrapEnabled",
        "SupportEnabled",
        "ChatAIEnabled",
        "KnowledgeBaseEnabled",
        "UpdatesTabEnabled",
        "AgentHomeTab",
        "ZeroTouchEnabled",
        "KnowledgeEmbeddingEnabled",
        // Kill switches de feature (CFG-A). Sem entrar aqui o campo não aparece
        // na UI nem é reconhecido em locks/herança.
        "DepartmentAuthShadowMode",
        "PushEnabled",
        "WingetCatalogEnabled",
        "RemoteAccessEnabled",
        "RemoteAccessRecordingEnabled",
        "RemoteAccessRecordingAutoDeleteExpired",
        "AutomaticLoggingEnabled",
        "OpenTelemetryEnabled",
        "MigrationsRunOnStartup",
        "RealtimeContractMode",
        "AppStorePolicy",
        "InventoryIntervalHours",
        "AgentUpdatePolicyJson",
        "MaintenanceWindowJson",
        // "McpToolPolicyJson" removido do catálogo: o campo ficou LEGADO.
        // A política de tools MCP do chat é gerenciada apenas em
        // /settings/mcp-tools (mcp_tool_policies) e o payload do agent deriva
        // a allowlist dessas políticas (GetAgentConfigurationHandler).
        "AIIntegrationSettingsJson",
        "TicketAttachmentSettingsJson",
        // Configuração global dos sensores de monitoramento (intervalo default,
        // kill switch, auto-ticket e notificações).
        "SensorsSettingsJson",
        "BackgroundProcessingSettingsJson",
        "AgentHeartbeatIntervalSeconds",
        "AgentOnlineGraceSeconds",
        "RemoteConsentMode",
        "RemoteConsentTimeoutSeconds",
        "RemoteConsentTimeoutAction",
        "RemoteConsentShowBanner",
        "RemoteConsentMessage",
        "RemoteConsentBannerText",
        "NatsAuthEnabled",
        "NatsAgentJwtTtlMinutes",
        "NatsUserJwtTtlMinutes",
        "NatsServerHostInternal",
        "NatsServerHostExternal",
        "NatsUseWssExternal",
        "NatsWebSocketExternalUrl"
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
        "EmbeddingTicketAnswersEnabled",
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
