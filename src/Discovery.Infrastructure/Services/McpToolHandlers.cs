namespace Discovery.Infrastructure.Services;

/// <summary>
/// Handlers adicionais de MCP tools.
/// NOTA: Os handlers padrão (knowledge_search, time.current, sequential_thinking, memory.search)
/// são registrados diretamente no construtor do McpToolExecutor. Este arquivo existe apenas para
/// eventuais handlers que precisem de injeção de dependências externas não disponíveis no McpToolExecutor.
/// </summary>
public static class McpToolHandlers
{
    /// <summary>
    /// Registra handlers que dependem de serviços externos ao McpToolExecutor.
    /// Chamado no Program.cs após o build do container DI.
    /// </summary>
    public static void RegisterBuiltInHandlers(IMcpToolExecutor executor)
    {
        // Todos os handlers built-in são registrados no construtor do McpToolExecutor.
        // Adicione aqui apenas handlers que precisem de IServiceProvider ou serviços externos.
    }
}
