using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Testes da tokenização de busca keyword da KB. Contexto: a query inteira era
/// um único ILIKE ("%como configurar a vpn%") e nunca encontrava artigos.
/// </summary>
public class KnowledgeKeywordQueryTests
{
    [Test]
    public void BuildTerms_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.That(KnowledgeKeywordQuery.BuildTerms(""), Is.Empty);
        Assert.That(KnowledgeKeywordQuery.BuildTerms("   "), Is.Empty);
        Assert.That(KnowledgeKeywordQuery.BuildTerms(null), Is.Empty);
    }

    [Test]
    public void BuildTerms_NaturalLanguage_SplitsTermsAndDropsStopwords()
    {
        var terms = KnowledgeKeywordQuery.BuildTerms("Como configurar a VPN?");

        Assert.That(terms, Does.Contain("configurar"));
        Assert.That(terms, Does.Contain("vpn"));
        Assert.That(terms, Does.Not.Contain("como"));
    }

    [Test]
    public void BuildTerms_RemovesAccentsForMatching()
    {
        var terms = KnowledgeKeywordQuery.BuildTerms("Política de senhas");

        Assert.That(terms, Does.Contain("politica"));
        Assert.That(terms, Does.Contain("senhas"));
    }

    [Test]
    public void BuildTerms_DropsShortTokens()
    {
        var terms = KnowledgeKeywordQuery.BuildTerms("a de do um x configurar vpn");

        Assert.That(terms, Does.Contain("configurar"));
        Assert.That(terms, Does.Contain("vpn"));
        Assert.That(terms, Does.Not.Contain("a"));
        Assert.That(terms, Does.Not.Contain("de"));
        Assert.That(terms.Count, Is.EqualTo(2));
    }

    [Test]
    public void BuildTerms_DeduplicatesAndCapsAtMaxTerms()
    {
        var terms = KnowledgeKeywordQuery.BuildTerms(
            "alfa alfa beta gama delta epsilon zeta eta theta iota kappa lambda");

        Assert.That(terms.Count, Is.EqualTo(KnowledgeKeywordQuery.MaxTerms));
        Assert.That(terms.Distinct().Count(), Is.EqualTo(terms.Count));
    }
}
