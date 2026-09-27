using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Sinais determinísticos do chamado usados pela triagem por IA (tags e
/// dificuldade). Precisam ser estáveis sem depender do provedor de IA.
/// </summary>
public class TicketSignalExtractorTests
{
    private static Ticket NewTicket(
        string title, string description, TicketPriority priority = TicketPriority.Medium, string? category = null)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = priority,
            Category = category,
            WorkflowStateId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public void ExtractTags_IncludesCategoryAndRelevantTokens()
    {
        var ticket = NewTicket("Impressora fiscal nao imprime", "A impressora HP do caixa parou de imprimir cupons", category: "Hardware");

        var tags = TicketSignalExtractor.ExtractTags(ticket);

        Assert.That(tags, Does.Contain("hardware"));
        Assert.That(tags, Does.Contain("impressora"));
    }

    [Test]
    public void EstimateDifficulty_RaisesLevel_ForHighPriorityAndComplexKeywords()
    {
        var simple = TicketSignalExtractor.EstimateDifficulty(
            NewTicket("Reset de senha", "Usuario esqueceu a senha do dominio", TicketPriority.Low), []);
        var complex = TicketSignalExtractor.EstimateDifficulty(
            NewTicket("Servidor de banco de dados fora do ar",
                "A instabilidade no cluster de storage derrubou o banco de dados", TicketPriority.Critical), []);

        Assert.That(complex.Level, Is.GreaterThan(simple.Level));
    }

    [Test]
    public void EstimateDifficulty_IsClampedBetweenOneAndFive()
    {
        var ticket = NewTicket("Servidor de rede banco de dados storage backup dominio",
            new string('x', 2000), TicketPriority.Critical);

        var (level, _) = TicketSignalExtractor.EstimateDifficulty(ticket, []);

        Assert.That(level, Is.InRange(1, 5));
    }

    [Test]
    public void EstimateDifficulty_LowersLevel_WhenThereIsRecurringHistory()
    {
        var ticket = NewTicket("Erro no ERP", "Falha ao emitir nota", TicketPriority.High, "ERP");
        var history = new List<TechnicianAffinityHit>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Erro no ERP", 0.9),
            new(Guid.NewGuid(), Guid.NewGuid(), "Falha no ERP", 0.8),
            new(Guid.NewGuid(), Guid.NewGuid(), "ERP lento", 0.7)
        };

        var withoutHistory = TicketSignalExtractor.EstimateDifficulty(ticket, []);
        var withHistory = TicketSignalExtractor.EstimateDifficulty(ticket, history);

        Assert.That(withHistory.Level, Is.LessThan(withoutHistory.Level));
    }
}
