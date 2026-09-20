namespace Discovery.Core.Helpers;

/// <summary>
/// Normalização de versão/commit reportados pelo agent.
///
/// Builds locais (wails build / go build sem ldflags) reportam placeholders —
/// buildinfo.Version="0.0.0", app.Version="dev", Commit="unknown" — e builds
/// antigos persistiram esses valores como se fossem reais (ex.: tela do console
/// exibindo "Versão do Agente: dev" sem commit). Esta classe centraliza o
/// filtro: placeholders NUNCA sobrescrevem um valor bom já conhecido nem são
/// exibidos como versão real.
/// </summary>
public static class AgentVersionNormalizer
{
    private static readonly string[] Placeholders = ["dev", "unknown", "0.0.0"];

    /// <summary>True quando o valor é vazio ou placeholder de build sem injeção de versão/commit.</summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var trimmed = value.Trim();
        foreach (var placeholder in Placeholders)
        {
            if (string.Equals(trimmed, placeholder, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Retorna o primeiro candidato com valor real (não-placeholder), aparado —
    /// ou null quando todos forem placeholders. Ordem esperada de chamada:
    /// heartbeat (mais fresco) e depois entidade (último hardware report).
    /// </summary>
    public static string? PickVersion(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!IsPlaceholder(candidate))
                return candidate!.Trim();
        }

        return null;
    }

    /// <summary>Commit normalizado para persistência/exibição — placeholder vira null.</summary>
    public static string? NormalizeCommit(string? commit)
        => IsPlaceholder(commit) ? null : commit!.Trim();
}
