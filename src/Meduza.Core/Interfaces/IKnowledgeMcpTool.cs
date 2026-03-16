using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

public interface IKnowledgeMcpTool
{
    /// <summary>
    /// Executa busca na KB para uso como MCP tool call pelo LLM.
    /// Retorna JSON com os chunks mais relevantes no escopo.
    /// </summary>
    Task<string> ExecuteAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        int maxResults = 3,
        CancellationToken ct = default);

    /// <summary>
    /// Versão otimizada: usa AIIntegrationSettings já resolvidas (evita recarregar config)
    /// e aceita IDs de artigos a excluir (evita duplicar chunks já injetados no system prompt).
    /// </summary>
    Task<string> ExecuteWithSettingsAsync(
        Guid? clientId,
        Guid? siteId,
        string query,
        AIIntegrationSettings aiSettings,
        IReadOnlyCollection<Guid>? excludeArticleIds = null,
        int maxResults = 3,
        CancellationToken ct = default);
}
