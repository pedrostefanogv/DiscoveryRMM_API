using Meduza.Core.Interfaces;

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
}
