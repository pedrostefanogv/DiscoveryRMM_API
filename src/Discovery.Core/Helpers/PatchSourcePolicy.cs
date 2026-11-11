using Discovery.Core.Cqrs.Patches;

namespace Discovery.Core.Helpers;

/// <summary>
/// Consentimento de ORIGEM de patch (WU-19/WU-20, decisão D22).
///
/// A política de patches passou a cobrir dois universos:
///  • <c>os</c> — Windows Update (WUA/QFE), comportamento histórico;
///  • <c>third_party</c> — apps de terceiros (Chocolatey/winget), opt-in.
///
/// Duplo consentimento: para auto-aprovar/instalar app de terceiros é preciso
/// (1) "third_party" em <c>Sources</c> E (2) <c>AllowThirdPartyApps</c> ligado.
/// Ausência de <c>Sources</c> significa "os" apenas — nenhuma política antiga
/// passa a instalar terceiros por upgrade.
/// </summary>
public static class PatchSourcePolicy
{
    public const string Os = "os";
    public const string ThirdParty = "third_party";
    // Upgrade de versao sintetizado pela avaliacao WaaS: visivel no inventario,
    // sem pacote WUA para instalar pelo RMM.
    public const string WaaSFeature = "waas";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Os, ThirdParty
    };

    /// <summary>Verdadeiro quando o item e o upgrade de versao sintetizado (WaaS).</summary>
    public static bool IsWaaSFeatureSource(string? source) =>
        !string.IsNullOrWhiteSpace(source) &&
        string.Equals(source.Trim(), WaaSFeature, StringComparison.OrdinalIgnoreCase);

    /// <summary>Verdadeiro quando a origem do update é de aplicativo de terceiros.</summary>
    public static bool IsThirdPartySource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        var normalized = source.Trim();
        return string.Equals(normalized, "choco", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "winget", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Origens efetivas da política (vazio/ausente = só "os").</summary>
    public static IReadOnlyList<string> EffectiveSources(string? sourcesJson)
    {
        var parsed = PatchJson.ParseStringArray(sourcesJson)
            .Where(value => Known.Contains(value))
            .Select(value => value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parsed.Count == 0 ? [Os] : parsed;
    }

    /// <summary>A política permite updates de OS?</summary>
    public static bool AllowsOs(string? sourcesJson) =>
        EffectiveSources(sourcesJson).Contains(Os, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A política habilita a ORIGEM de apps de terceiros (1º consentimento, D22)?
    ///
    /// Este gate é de VISIBILIDADE/REPORT, não de automação: com a origem
    /// desativada (padrão — ausente = só "os") os apps choco/winget não aparecem
    /// nas visões de frota (inventário, pendências, conformidade), não entram nos
    /// contadores dos snapshots e o agente nem os coleta. A automação continua
    /// exigindo o duplo consentimento (<see cref="ThirdPartyConsented"/>).
    /// </summary>
    public static bool AllowsThirdParty(string? sourcesJson) =>
        EffectiveSources(sourcesJson).Contains(ThirdParty, StringComparer.OrdinalIgnoreCase);

    /// <summary>Duplo consentimento para terceiros (Sources + AllowThirdPartyApps).</summary>
    public static bool ThirdPartyConsented(string? sourcesJson, bool allowThirdPartyApps) =>
        allowThirdPartyApps &&
        EffectiveSources(sourcesJson).Contains(ThirdParty, StringComparer.OrdinalIgnoreCase);
}
