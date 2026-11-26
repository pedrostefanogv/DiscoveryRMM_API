namespace Discovery.Core.Configuration;

public static class ConfigurationFieldCatalog
{
    public static readonly string[] ManagedFields =
    [
        "RecoveryEnabled",
        "DiscoveryEnabled",
        "P2PFilesEnabled",
        // Cofre TPM de credenciais de conexão do agent (servidor → cliente → site).
        // Opt-in (default false). Sem entrar aqui o campo não aparece na UI nem é
        // reconhecido em locks/herança — exatamente como os kill switches abaixo.
        "TpmRecoveryEnabled",
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
        // Teto global herdável de chamadas de tools MCP do chat de IA
        // (servidor → cliente → site). Sem entrar aqui o campo não aparece na UI
        // nem é reconhecido em locks/herança.
        "McpToolCallsPerMinute",
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
        "NatsWebSocketExternalUrl",
        // Allowlist de proxy/túnel do acesso remoto, herdável servidor → cliente
        // → site (Blob JSON). Sem entrar aqui o campo não aparece na UI nem é
        // reconhecido em locks/herança — exatamente como os kill switches acima.
        // Antes disto a allowlist só existia no appsettings.json e nenhuma tela
        // conseguia alterá-la (o default vazio bloqueava todo destino).
        "RemoteProxyPolicyJson",
        // Liga/desliga o uso das entradas cadastradas em escopo GLOBAL. Nasce
        // DESLIGADO: o cadastro global fica pré-pronto, mas inerte, até o
        // operador liberar explicitamente. Entradas de Cliente/Site não são
        // afetadas por esta flag.
        "RemoteProxyGlobalEntriesEnabled",
        // WebRTC do acesso remoto: política (STUN/TURN/kinds/fallback) + kill
        // switch, ambos herdáveis servidor → cliente → site. Sem entrar aqui os
        // campos não aparecem na UI nem são reconhecidos em locks/herança —
        // exatamente como os kill switches acima. Default DESLIGADO: o caminho
        // NATS continua sendo o caminho, e o WebRTC é ativado explicitamente.
        "RemoteAccessWebRtcPolicyJson",
        "RemoteAccessWebRtcEnabled"
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
