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

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Os, ThirdParty
    };

    /// <summary>Verdadeiro quando a origem do update é de aplicativo de terceiros.</summary>
    public static bool IsThirdPartySource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        var normalized = source.Trim();
        return string.Equals(normalized, "choco", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "winget", StringComparison.OrdinalIgnoreCase) ||
               // msi: catálogo próprio com download direto (WU-21). É origem de
               // terceiros para efeito de consentimento e curadoria.
               string.Equals(normalized, "msi", StringComparison.OrdinalIgnoreCase);
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
    /// Origem que EXIGE entrada ativa no catálogo para auto-aprovação.
    ///
    /// Só o caminho de DOWNLOAD DIRETO (MSI corporativo) precisa disso: o agente
    /// baixa da URL do fornecedor, então hash/assinatura vêm do catálogo.
    /// winget/choco são gerenciadores de pacote — validam checksum/assinatura no
    /// próprio feed; exigir catálogo ali transformaria toda atualização de app em
    /// aprovação manual (beco sem saída operacional).
    /// </summary>
    public static bool RequiresCuratedCatalog(string? source) =>
        string.Equals(source?.Trim(), "msi", StringComparison.OrdinalIgnoreCase);

    /// <summary>Duplo consentimento para terceiros (Sources + AllowThirdPartyApps).</summary>
    public static bool ThirdPartyConsented(string? sourcesJson, bool allowThirdPartyApps) =>
        allowThirdPartyApps &&
        EffectiveSources(sourcesJson).Contains(ThirdParty, StringComparer.OrdinalIgnoreCase);
}
