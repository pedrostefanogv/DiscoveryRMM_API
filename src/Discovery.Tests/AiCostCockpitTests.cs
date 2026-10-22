using Discovery.Core.Cqrs.AiCost;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Painel de custo de IA por cliente: agregação por cliente, topTools e
/// leitura de budgetUsdMonthly do override JSON do ClientConfiguration.
/// </summary>
[TestFixture]
public class AiCostCockpitTests
{
    [Test]
    public async Task Aggregates_TokensAndCostsPerClient()
    {
        await using var db = NewDb();
        var clientA = new Client { Id = Guid.NewGuid(), Name = "Cliente A" };
        var clientB = new Client { Id = Guid.NewGuid(), Name = "Cliente B" };
        var sessionA = NewSession(clientA.Id);
        var sessionB = NewSession(clientB.Id);
        db.Clients.AddRange(clientA, clientB);
        db.AiChatSessions.AddRange(sessionA, sessionB);
        db.AiChatMessages.AddRange(
            Msg(sessionA.Id, "assistant", 100),
            Msg(sessionA.Id, "assistant", 50),
            Msg(sessionA.Id, "user", 200),
            Msg(sessionB.Id, "assistant", 10),
            Msg(sessionB.Id, "tool", null, toolName: "restart-agent"));
        await db.SaveChangesAsync();

        var handler = new AiCostCockpitQueryHandler(db);
        var result = await handler.Handle(new GetAiCostCockpitQuery(), default);

        Assert.That(result.IsSuccess, Is.True);
        var cockpit = result.Value;

        Assert.Multiple(() =>
        {
            Assert.That(cockpit.Totals.Requests, Is.EqualTo(3), "uma requisição LLM por mensagem assistant com tokens");
            Assert.That(cockpit.Totals.OutputTokens, Is.EqualTo(160), "tokens persistidos são da resposta do LLM (assistant)");
            Assert.That(cockpit.Totals.InputTokens, Is.EqualTo(200), "tokens de mensagens user quando presentes");
            Assert.That(cockpit.Totals.CostUsd, Is.GreaterThan(0));
        });

        var rowA = cockpit.ByClient.Single(c => c.ClientId == clientA.Id);
        var rowB = cockpit.ByClient.Single(c => c.ClientId == clientB.Id);
        Assert.Multiple(() =>
        {
            Assert.That(rowA.ClientName, Is.EqualTo("Cliente A"));
            Assert.That(rowA.Requests, Is.EqualTo(2));
            Assert.That(rowA.OutputTokens, Is.EqualTo(150));
            Assert.That(rowB.OutputTokens, Is.EqualTo(10));
            Assert.That(rowA.BudgetUsdMonthly, Is.Null, "sem budget configurado");
            Assert.That(cockpit.ByClient.First().ClientId, Is.EqualTo(clientA.Id), "ordenado por custo desc");
        });
    }

    [Test]
    public async Task TopTools_ListsToolCallsOrderedByCalls()
    {
        await using var db = NewDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "C" };
        var session = NewSession(client.Id);
        db.Clients.Add(client);
        db.AiChatSessions.Add(session);
        db.AiChatMessages.AddRange(
            Msg(session.Id, "tool", null, toolName: "restart-agent"),
            Msg(session.Id, "tool", null, toolName: "restart-agent"),
            Msg(session.Id, "tool", null, toolName: "list-services"));
        await db.SaveChangesAsync();

        var handler = new AiCostCockpitQueryHandler(db);
        var cockpit = (await handler.Handle(new GetAiCostCockpitQuery(), default)).Value;

        Assert.That(cockpit.TopTools.Select(x => x.Tool).ToList(), Is.EqualTo(new[] { "restart-agent", "list-services" }));
        Assert.That(cockpit.TopTools[0].Calls, Is.EqualTo(2));
    }

    [Test]
    public async Task BudgetUsdMonthly_ReadFromClientConfigurationOverrideJson()
    {
        await using var db = NewDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "C" };
        db.Clients.Add(client);
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            AIIntegrationSettingsJson = """{"budgetUsdMonthly":25.5}"""
        });
        var session = NewSession(client.Id);
        db.AiChatSessions.Add(session);
        db.AiChatMessages.Add(Msg(session.Id, "assistant", 40));
        await db.SaveChangesAsync();

        var handler = new AiCostCockpitQueryHandler(db);
        var cockpit = (await handler.Handle(new GetAiCostCockpitQuery(), default)).Value;

        Assert.That(cockpit.ByClient.Single().BudgetUsdMonthly, Is.EqualTo(25.5));
    }

    [Test]
    public async Task ClientFilter_ReturnsOnlyThatClient()
    {
        await using var db = NewDb();
        var clientA = new Client { Id = Guid.NewGuid(), Name = "A" };
        var clientB = new Client { Id = Guid.NewGuid(), Name = "B" };
        var sessionA = NewSession(clientA.Id);
        var sessionB = NewSession(clientB.Id);
        db.Clients.AddRange(clientA, clientB);
        db.AiChatSessions.AddRange(sessionA, sessionB);
        db.AiChatMessages.Add(Msg(sessionA.Id, "assistant", 100));
        db.AiChatMessages.Add(Msg(sessionB.Id, "assistant", 999));
        await db.SaveChangesAsync();

        var handler = new AiCostCockpitQueryHandler(db);
        var cockpit = (await handler.Handle(new GetAiCostCockpitQuery(clientA.Id), default)).Value;

        Assert.Multiple(() =>
        {
            Assert.That(cockpit.ByClient.Select(c => c.ClientId), Is.EqualTo(new[] { clientA.Id }));
            Assert.That(cockpit.Totals.OutputTokens, Is.EqualTo(100));
        });
    }

    [Test]
    public async Task MonthlyCostReader_SumsAssistantTokensOfCurrentMonth()
    {
        await using var db = NewDb();
        var client = new Client { Id = Guid.NewGuid(), Name = "C" };
        var session = NewSession(client.Id);
        db.Clients.Add(client);
        db.AiChatSessions.Add(session);
        db.AiChatMessages.Add(Msg(session.Id, "assistant", 1_000_000)); // 1M output = $0.60
        await db.SaveChangesAsync();

        var handler = new AiCostCockpitQueryHandler(db);
        var cost = await handler.GetMonthToDateCostUsdAsync(client.Id);

        Assert.That(cost, Is.EqualTo(0.6).Within(0.0001));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static AiChatSession NewSession(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        SiteId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        CreatedByIp = "127.0.0.1",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(1)
    };

    private static AiChatMessage Msg(Guid sessionId, string role, int? tokens, string? toolName = null) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        SequenceNumber = 1,
        Role = role,
        Content = "x",
        TokensUsed = tokens,
        ToolName = toolName,
        CreatedAt = DateTime.UtcNow
    };

    private static AiCostTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ai-cost-{Guid.NewGuid():N}")
            .Options;
        return new AiCostTestDbContext(options);
    }

    /// <summary>Contexto restrito (InMemory não suporta o modelo completo).</summary>
    private sealed class AiCostTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var included = new[]
            {
                typeof(AiChatMessage), typeof(AiChatSession),
                typeof(Client), typeof(ClientConfiguration)
            };

            foreach (var entityType in typeof(Discovery.Core.Entities.Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !included.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AiChatSession>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).ValueGeneratedNever();
                entity.HasMany(s => s.Messages).WithOne(m => m.Session).HasForeignKey(m => m.SessionId);
            });
            modelBuilder.Entity<AiChatMessage>(entity =>
            {
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<Client>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<ClientConfiguration>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Id).ValueGeneratedNever();
            });
        }
    }
}
