namespace Discovery.Core.Helpers;

/// <summary>
/// Monta padrões ILIKE com escape de curingas (`%`, `_`, `\`) para que o texto
/// do usuário não vire coringa. Centraliza o padrão já usado em tickets/logs.
/// </summary>
public static class LikePatternHelper
{
    /// <summary>Escapa curingas do termo informado.</summary>
    public static string Escape(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Padrão "contém": `%termo%` com curingas escapados.</summary>
    public static string Contains(string term) => $"%{Escape(term)}%";
}
