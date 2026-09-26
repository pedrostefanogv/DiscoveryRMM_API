namespace Discovery.Core.Interfaces;

/// <summary>
/// MCP tool "knowledge_list": lista o catálogo de artigos PUBLICADOS da base
/// de conhecimento acessíveis a um escopo (site → client → global).
/// Complementa o <see cref="IKnowledgeMcpTool"/> (busca por assunto): perguntas
/// do tipo "quais artigos existem?" não têm query e não são respondíveis por busca.
/// </summary>
public interface IKnowledgeListMcpTool
{
    /// <summary>
    /// Lista artigos publicados no escopo, opcionalmente filtrando por categoria.
    /// Retorna JSON pronto para o LLM (nunca lança: erros viram JSON com "error").
    /// </summary>
    Task<string> ExecuteAsync(
        Guid? clientId,
        Guid? siteId,
        string? category,
        int limit,
        CancellationToken ct = default);
}
