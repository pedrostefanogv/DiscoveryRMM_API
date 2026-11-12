namespace Discovery.Core.Configuration;

/// <summary>
/// Fonte ÚNICA do rate limit de chamadas das MCP tools do chat de IA.
///
/// Duas grandezas convivem aqui:
///  • o limite POR TOOL (mcp_tool_policies.max_calls_per_minute), editável em
///    /settings/mcp-tools por escopo (global → cliente → site → agente);
///  • o TETO GLOBAL herdável (McpToolCallsPerMinute, servidor → cliente → site),
///    editável nas telas de configuração, que limita a soma das chamadas de
///    TODAS as tools do escopo.
///
/// O limite efetivo de uma tool é sempre o MENOR dos dois
/// (<c>Math.Min(limiteDaTool, tetoGlobal)</c>) — o teto global é um piso, nunca
/// um aumento. Mesmo precedente dos kill switches herdáveis (M286) e da catraca
/// UpdatesTabEnabled/KnowledgeEmbeddingEnabled: um valor inválido ou ausente cai
/// no DEFAULT, sem nunca desligar o limite por acidente.
/// </summary>
public static class McpToolRateLimitCatalog
{
    /// <summary>
    /// Padrão de chamadas por minuto por tool MCP (substitui os antigos 5 do
    /// servidor e 10 do agente). É também o default do teto global do servidor.
    /// </summary>
    public const int DefaultCallsPerMinute = 20;

    /// <summary>Mínimo aceito. 0 NÃO é aceito: o rate limit não é desligável por aqui.</summary>
    public const int MinCallsPerMinute = 1;

    /// <summary>
    /// Máximo aceito — o MESMO teto aplicado pelo McpToolsController ao salvar a
    /// política de uma tool individual (Math.Min(valor, 600)).
    /// </summary>
    public const int MaxCallsPerMinute = 600;

    /// <summary>
    /// Sanitiza um valor vindo de configuração/política: dentro de
    /// [<see cref="MinCallsPerMinute"/>, <see cref="MaxCallsPerMinute"/>] devolve o
    /// próprio valor; null, 0, negativo ou acima do teto devolve
    /// <see cref="DefaultCallsPerMinute"/>. Nunca devolve um valor que desligue o
    /// limite silenciosamente.
    /// </summary>
    public static int Normalize(int? value) =>
        value is >= MinCallsPerMinute and <= MaxCallsPerMinute ? value.Value : DefaultCallsPerMinute;

    /// <summary>Normaliza uma política já lida (nunca null).</summary>
    public static int Normalize(int value) => Normalize((int?)value);

    /// <summary>
    /// Limite EFETIVO de uma tool: o menor entre o limite por tool e o teto
    /// global. Um teto global &lt;= 0 significa "sem teto global" (herança ausente
    /// em bases antigas) e deixa o limite da tool valer integralmente — o teto
    /// NUNCA cria limite onde não havia.
    ///
    /// PONTO ÚNICO DA REGRA: governança (chat) e executor (tools de servidor)
    /// chamam ESTE método. Não reimplemente o Math.Min em nenhum dos dois.
    /// </summary>
    public static int Effective(int toolMaxCallsPerMinute, int globalMaxCallsPerMinute) =>
        globalMaxCallsPerMinute <= 0 ? toolMaxCallsPerMinute : Math.Min(toolMaxCallsPerMinute, globalMaxCallsPerMinute);
}
