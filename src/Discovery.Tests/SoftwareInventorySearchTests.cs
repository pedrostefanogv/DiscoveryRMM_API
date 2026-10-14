using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Busca e identidade do inventário de software.
///
/// Contexto do defeito (2026-10-14): buscar "Foxit Editor" não encontrava
/// "Foxit PDF Editor" (a busca era um único LIKE %frase%) e o mesmo produto
/// aparecia duas vezes na lista, porque o fingerprint do catálogo incluía
/// install_id/serial/source — voláteis por máquina e por hive de registro.
/// </summary>
[TestFixture]
public class SoftwareInventorySearchTests
{
    // ── Termos de busca ─────────────────────────────────────────────────

    [Test]
    public void Parse_SplitsPhraseIntoSeparateTerms()
    {
        var terms = SoftwareSearchTerms.Parse("Foxit Editor");

        Assert.That(terms, Is.EqualTo(new[] { "Foxit", "Editor" }));
    }

    [Test]
    public void BuildPatterns_ProducesOnePatternPerTerm()
    {
        var patterns = SoftwareSearchTerms.BuildPatterns("Foxit Editor");

        Assert.That(patterns, Is.EqualTo(new[] { "%Foxit%", "%Editor%" }));
    }

    [Test]
    public void Parse_IgnoresRepeatedAndCaseInsensitiveDuplicates()
    {
        var terms = SoftwareSearchTerms.Parse("  foxit   FOXIT  Editor ");

        Assert.That(terms, Is.EqualTo(new[] { "foxit", "Editor" }));
    }

    [Test]
    public void Parse_KeepsVersionTokensIntact()
    {
        var terms = SoftwareSearchTerms.Parse("Foxit 14.0.7.33751");

        Assert.That(terms, Is.EqualTo(new[] { "Foxit", "14.0.7.33751" }));
    }

    [Test]
    public void Parse_EmptyOrBlankSearchYieldsNothing()
    {
        Assert.That(SoftwareSearchTerms.Parse(null), Is.Empty);
        Assert.That(SoftwareSearchTerms.Parse("   "), Is.Empty);
        Assert.That(SoftwareSearchTerms.BuildPatterns("   "), Is.Null);
    }

    [Test]
    public void Parse_CapsTheNumberOfTerms()
    {
        var search = string.Join(' ', Enumerable.Range(0, 20).Select(i => $"t{i}"));

        Assert.That(SoftwareSearchTerms.Parse(search), Has.Count.EqualTo(SoftwareSearchTerms.MaxTerms));
    }

    [Test]
    public void EscapeLikeWildcards_NeutralizesUserWildcards()
    {
        Assert.That(SoftwareSearchTerms.EscapeLikeWildcards("100%"), Is.EqualTo(@"100\%"));
        Assert.That(SoftwareSearchTerms.EscapeLikeWildcards("a_b"), Is.EqualTo(@"a\_b"));
        Assert.That(SoftwareSearchTerms.EscapeLikeWildcards(@"c:\dir"), Is.EqualTo(@"c:\\dir"));
        Assert.That(SoftwareSearchTerms.ToILikePattern("100%"), Is.EqualTo(@"%100\%%"));
    }

    [Test]
    public void TwoTermSearch_MatchesNameThatOnlyContainsBothWordsSeparately()
    {
        // O caso real: "Foxit Editor" deve casar "Foxit PDF Editor".
        var name = "Foxit PDF Editor";

        var matches = SoftwareSearchTerms.Parse("Foxit Editor")
            .All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

        Assert.That(matches, Is.True);
    }

    [Test]
    public void SinglePhraseSearch_DoesNotMatchNameWithWordsApart()
    {
        // Comportamento antigo (o bug): a frase inteira não é substring do nome.
        Assert.That("Foxit PDF Editor".Contains("Foxit Editor", StringComparison.OrdinalIgnoreCase), Is.False);
    }

    // ── Identidade canônica ─────────────────────────────────────────────

    [Test]
    public void NormalizeName_TrimsLowercasesAndCollapsesSpaces()
    {
        Assert.That(SoftwareIdentity.NormalizeName("  Foxit   PDF\tEditor "), Is.EqualTo("foxit pdf editor"));
    }

    [Test]
    public void BuildFingerprint_IsStableAcrossCasingAndSpacing()
    {
        var a = SoftwareIdentity.BuildFingerprint("Foxit PDF Editor");
        var b = SoftwareIdentity.BuildFingerprint("  foxit   pdf editor  ");

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Has.Length.EqualTo(64));
    }

    [Test]
    public void BuildFingerprint_DistinguishesDifferentProducts()
    {
        Assert.That(
            SoftwareIdentity.BuildFingerprint("Foxit PDF Editor"),
            Is.Not.EqualTo(SoftwareIdentity.BuildFingerprint("Foxit PDF Reader")));
    }

    [Test]
    public void NormalizePublisher_TreatsLegacyPlaceholderAsUnknown()
    {
        Assert.That(SoftwareIdentity.NormalizePublisher("Sem fabricante"), Is.Null);
        Assert.That(SoftwareIdentity.NormalizePublisher("  "), Is.Null);
        Assert.That(SoftwareIdentity.NormalizePublisher(null), Is.Null);
        Assert.That(SoftwareIdentity.NormalizePublisher(" Foxit Software Inc. "), Is.EqualTo("Foxit Software Inc."));
        Assert.That(SoftwareIdentity.HasKnownPublisher("SEM FABRICANTE"), Is.False);
    }

    [Test]
    public void CompareVersions_OrdersByNumericSegments()
    {
        Assert.That(SoftwareIdentity.CompareVersions("125.0.0.0", "124.0.0.0"), Is.GreaterThan(0));
        Assert.That(SoftwareIdentity.CompareVersions("124.0.0.0", "125.0.0.0"), Is.LessThan(0));
        Assert.That(SoftwareIdentity.CompareVersions("14.0.7.33751", "14.0.7.3375"), Is.GreaterThan(0),
            "compara por segmento, não como texto");
        Assert.That(SoftwareIdentity.CompareVersions("2026.2.1.39815", "2026.1.3.36551"), Is.GreaterThan(0));
        Assert.That(SoftwareIdentity.CompareVersions("1.0.0", "1.0.0"), Is.EqualTo(0));
    }

    [Test]
    public void PreferNewestVersion_KeepsTheHighestAndIgnoresEmpties()
    {
        // Caso real: WebView2 em hives 32/64 bits (124.x e 125.x). O osquery manda a
        // lista ordenada por nome|versão, então "primeiro não-vazio" pegava a antiga.
        Assert.That(SoftwareIdentity.PreferNewestVersion("124.0.0.0", "125.0.0.0"), Is.EqualTo("125.0.0.0"));
        Assert.That(SoftwareIdentity.PreferNewestVersion("125.0.0.0", "124.0.0.0"), Is.EqualTo("125.0.0.0"));
        Assert.That(SoftwareIdentity.PreferNewestVersion(null, "14.0.7"), Is.EqualTo("14.0.7"));
        Assert.That(SoftwareIdentity.PreferNewestVersion("14.0.7", null), Is.EqualTo("14.0.7"));
        Assert.That(SoftwareIdentity.PreferNewestVersion("  ", "14.0.7"), Is.EqualTo("14.0.7"));
    }

    // ── Tradução da busca multi-termo para SQL ──────────────────────────

    /// <summary>
    /// O provider InMemory não traduz ILike; este teste compila a MESMA
    /// composição usada por AgentSoftwareRepository (um Where por termo, com
    /// escape explícito) contra o provider Npgsql, sem abrir conexão.
    /// </summary>
    [Test]
    public void MultiTermSearch_TranslatesToConjunctionOfILike()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_translation_only;Username=none;Password=none",
                npgsql => npgsql.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        IQueryable<SoftwareCatalog> query =
            from inv in db.AgentSoftwareInventories
            join catalog in db.SoftwareCatalogs on inv.SoftwareId equals catalog.Id
            where inv.IsPresent
            select catalog;

        var patterns = SoftwareSearchTerms.BuildPatterns("Foxit Editor");
        Assert.That(patterns, Is.Not.Null);

        foreach (var pattern in patterns!)
        {
            var term = pattern;
            query = query.Where(catalog =>
                EF.Functions.ILike(catalog.Name, term, SoftwareSearchTerms.EscapeCharacter) ||
                (catalog.Publisher != null &&
                 EF.Functions.ILike(catalog.Publisher, term, SoftwareSearchTerms.EscapeCharacter)));
        }

        var sql = query.ToQueryString();

        Assert.That(sql, Does.Contain("ILIKE"));
        Assert.That(sql, Does.Contain("ESCAPE"));
        // Dois termos × dois campos = 4 comparações ILIKE no SQL gerado.
        Assert.That(sql.Split("ILIKE").Length - 1, Is.GreaterThanOrEqualTo(4));
        Assert.That(sql, Does.Contain("software_catalog"));
    }
}
