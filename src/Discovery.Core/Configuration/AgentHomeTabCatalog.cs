namespace Discovery.Core.Configuration;

/// <summary>
/// Catálogo da "página inicial" do agent (agentHomeTab): qual aba do painel o
/// agent abre por padrão. A configuração é global no servidor e pode ser
/// sobrescrita (herdada) por cliente e por site.
///
/// Os ids abaixo são o contrato com o painel do agent (frontend Wails) — não
/// renomeie sem atualizar o agent. Se a aba configurada estiver desabilitada
/// para o agent, ele cai para <see cref="DefaultTab"/> (Status).
/// </summary>
public static class AgentHomeTabCatalog
{
    /// <summary>Aba usada como padrão e como fallback quando a aba configurada está indisponível.</summary>
    public const string DefaultTab = "status";

    /// <summary>
    /// Ids aceitos (ordem estável para mensagens de erro e UI). Exposto como
    /// IReadOnlyList para não ser mutado por acidente; é a fonte de verdade do
    /// contrato e é publicado na metadata de configuração (agentHomeTabOptions).
    /// </summary>
    public static readonly IReadOnlyList<string> ValidTabs =
    [
        "status",
        "store",
        "updates",
        "chat",
        "support",
        "knowledge"
    ];

    private static readonly HashSet<string> ValidTabSet = new(ValidTabs, StringComparer.OrdinalIgnoreCase);

    /// <summary>Indica se o id informado é uma aba aceita (ignora caixa/espaços).</summary>
    public static bool IsValid(string? tab)
        => !string.IsNullOrWhiteSpace(tab) && ValidTabSet.Contains(tab.Trim());

    /// <summary>
    /// Normaliza o valor: devolve o id em minúsculas quando válido; caso contrário
    /// (nulo, vazio ou desconhecido) devolve <see cref="DefaultTab"/>.
    /// </summary>
    public static string Normalize(string? tab)
        => IsValid(tab) ? tab!.Trim().ToLowerInvariant() : DefaultTab;
}
