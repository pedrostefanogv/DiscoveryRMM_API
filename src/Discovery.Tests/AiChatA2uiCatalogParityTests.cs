using System.Text.RegularExpressions;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Paridade entre o catálogo ACEITO pelo servidor (AiChatA2uiValidator) e o
/// catálogo EMBARCADO no renderer (a2ui-bundle.js).
///
/// Por que existe: o validador descarta a surface INTEIRA quando encontra um
/// componente que ele não conhece. Se o bundle ganhar um componente novo e a
/// lista do validador não for atualizada, todos os cards que o usarem somem — e
/// nenhum outro teste unitário pega isso. O teste roda quando o checkout do
/// agente (Discovery/) está ao lado do da API; sem ele, é IGNORADO.
/// </summary>
public class AiChatA2uiCatalogParityTests
{
    private static string? FindBundle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Discovery", "src", "frontend", "a2ui-bundle.js");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static HashSet<string> BundleTags(string bundle)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(bundle, @"(?:t4|customElements\.define)\(\s*""(a2ui-[a-z0-9-]+)"""))
            tags.Add(m.Groups[1].Value);
        // a2ui-surface é o host da surface, não um componente do catálogo.
        tags.Remove("a2ui-surface");
        return tags;
    }

    /// <summary>
    /// a2ui-basic-textfield e a2ui-textfield comparam iguais: remove o prefixo
    /// do catálogo e os hífens.
    /// </summary>
    private static string NormalizeTag(string tag) =>
        tag.Replace("a2ui-", "", StringComparison.Ordinal)
            .Replace("basic-", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    private static HashSet<string> KnownNames() =>
        AiChatA2uiValidator.KnownComponentNames
            .Select(n => n.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    [Test]
    public void Bundle_DeclaresOnlyComponentsKnownByTheValidator()
    {
        var bundlePath = FindBundle();
        if (bundlePath == null)
            Assert.Ignore("checkout do agente (Discovery/src/frontend/a2ui-bundle.js) não encontrado ao lado da API");

        var known = KnownNames();
        var unknown = BundleTags(File.ReadAllText(bundlePath))
            .Where(t => !known.Contains(NormalizeTag(t)))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.That(unknown, Is.Empty,
            "componente(s) do bundle ausente(s) em AiChatA2uiValidator.KnownComponents — o validador descartaria a surface inteira: " +
            string.Join(", ", unknown));
    }

    [Test]
    public void Validator_KnowsOnlyComponentsPresentInTheBundle()
    {
        var bundlePath = FindBundle();
        if (bundlePath == null)
            Assert.Ignore("checkout do agente (Discovery/src/frontend/a2ui-bundle.js) não encontrado ao lado da API");

        var bundleNames = BundleTags(File.ReadAllText(bundlePath)).Select(NormalizeTag).ToHashSet(StringComparer.Ordinal);
        var orphans = AiChatA2uiValidator.KnownComponentNames
            .Where(n => !bundleNames.Contains(n.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.That(orphans, Is.Empty,
            "componente(s) aceito(s) pelo validador mas ausente(s) no bundle — o card sairia sem eles: " +
            string.Join(", ", orphans));
    }
}
