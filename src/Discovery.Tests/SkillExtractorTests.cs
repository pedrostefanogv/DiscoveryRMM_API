using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Extração de competências a partir do histórico de chamados resolvidos.
/// </summary>
public class SkillExtractorTests
{
    private static SkillEvidenceTicket Ticket(string title, string description, string? category)
        => new(title, description, category);

    [Test]
    public void Extract_RespectsMinimumEvidence()
    {
        var tickets = new List<SkillEvidenceTicket>
        {
            Ticket("Falha de rede no switch", "A rede caiu", "Rede"),
            Ticket("Lentidao na rede", "Rede lenta com perda de pacotes", "Rede"),
            Ticket("Impressora offline", "Impressora nao imprime", "Hardware")
        };

        // Cada chamado conta 1x por tag (a categoria e o token 'rede' do mesmo
        // chamado são deduplicados): 'rede' aparece em 2 chamados.
        var (tags, evidence) = SkillExtractor.Extract(tickets, [], minEvidence: 2, maxTags: 10);

        Assert.That(tags, Does.Contain("rede"));
        Assert.That(tags, Does.Not.Contain("impressora"), "evidência mínima filtrou a tag com 1 chamado");
        Assert.That(evidence, Does.Contain("resolvedTickets"));
    }

    [Test]
    public void Extract_PreservesExistingTags()
    {
        var tickets = Enumerable.Range(0, 5)
            .Select(i => Ticket("Falha de rede " + i, "switch da rede", "Rede"))
            .ToList();

        var (tags, _) = SkillExtractor.Extract(tickets, ["rede"], minEvidence: 2, maxTags: 10);

        Assert.That(tags, Does.Not.Contain("rede"));
    }

    [Test]
    public void Extract_RespectsMaxTags()
    {
        var tickets = Enumerable.Range(0, 10)
            .Select(i => Ticket("Problema " + i, "detalhe " + i, "Categoria" + i))
            .ToList();

        var (tags, _) = SkillExtractor.Extract(tickets, [], minEvidence: 1, maxTags: 3);

        Assert.That(tags, Has.Count.LessThanOrEqualTo(3));
    }

    [Test]
    public void Extract_WithNoTickets_ReturnsEmpty()
    {
        var (tags, evidence) = SkillExtractor.Extract([], [], minEvidence: 3, maxTags: 10);

        Assert.That(tags, Is.Empty);
        Assert.That(evidence, Does.Contain("resolvedTickets"));
    }
}
