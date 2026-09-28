using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Guarda de tradução SQL do overview P2P.
///
/// O overview agrega no banco as duas amostras de fronteira por agente (mais
/// recente e mais antiga da janela). Se alguém reescrever essa consulta com algo
/// que o Npgsql não traduz, os testes com EF InMemory continuariam verdes e a
/// produção quebraria apenas em runtime. Este teste falha primeiro — e sem abrir
/// conexão, porque ToQueryString apenas executa a tradução.
/// </summary>
public class P2pOverviewQueryTranslationTests
{
    [Test]
    public void Overview_per_agent_boundary_query_translates_to_sql()
    {
        using var db = CreateNpgsqlContext();
        var cutoff = DateTime.UtcNow.AddHours(-24);

        var query = db.P2pAgentTelemetries.AsNoTracking()
            .Where(t => t.ReceivedAt >= cutoff)
            .GroupBy(t => t.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                MaxReceivedAt = g.Max(t => t.ReceivedAt),
                Latest = g.OrderByDescending(t => t.CollectedAt).ThenByDescending(t => t.ReceivedAt).First(),
                Earliest = g.OrderBy(t => t.CollectedAt).ThenBy(t => t.ReceivedAt).First()
            });

        var sql = query.ToQueryString();

        Assert.That(sql, Does.Contain("p2p_agent_telemetry"));
        // Filtro pelo índice novo (received_at) em vez de collected_at.
        Assert.That(sql, Does.Contain("received_at"));
        // Agregação limitada a 1 linha por agente de cada lado.
        Assert.That(sql, Does.Contain("ROW_NUMBER()"), "a fronteira por agente deve ser calculada no banco");
        Assert.That(sql, Does.Contain("row <= 1"));
    }

    [Test]
    public void Ranking_per_agent_query_translates_to_sql()
    {
        using var db = CreateNpgsqlContext();
        var cutoff = DateTime.UtcNow.AddHours(-24);

        var query = db.P2pAgentTelemetries.AsNoTracking()
            .Where(t => t.ReceivedAt >= cutoff)
            .GroupBy(t => t.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Latest = g.OrderByDescending(t => t.CollectedAt).ThenByDescending(t => t.ReceivedAt).First(),
                Earliest = g.OrderBy(t => t.CollectedAt).ThenBy(t => t.ReceivedAt).First(),
                QueueAvg = g.Average(t => (double)t.QueuedReplications),
                ActiveAvg = g.Average(t => (double)t.ActiveReplications)
            });

        var sql = query.ToQueryString();

        Assert.That(sql, Does.Contain("p2p_agent_telemetry"));
        Assert.That(sql, Does.Contain("ROW_NUMBER()"));
        Assert.That(sql, Does.Contain("avg("), "as médias devem ser agregadas no banco");
    }

    private static DiscoveryDbContext CreateNpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_test;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new DiscoveryDbContext(options);
    }
}
