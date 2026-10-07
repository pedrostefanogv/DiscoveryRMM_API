using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Memória persistente (tool MCP memory.search): a busca cobre conversas
/// ANTERIORES da mesma máquina, ignorando a conversa atual (que já está no
/// contexto do LLM), mensagens de tool e sessões apagadas/soft-deleted.
/// </summary>
[TestFixture]
public class AiChatMemorySearchTests
{
    [Test]
    public async Task SearchByAgent_ReturnsOnlyPreviousSessionsOfSameAgent()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var current = Guid.NewGuid();
        var otherAgentSession = Guid.NewGuid();
        var deletedSession = Guid.NewGuid();

        db.Add(Session(previous, agentId));
        db.Add(Session(current, agentId));
        db.Add(Session(otherAgentSession, otherAgentId));
        db.Add(Session(deletedSession, agentId, deletedAt: DateTime.UtcNow));

        db.AddRange(
            Message(previous, 1, "user", "A impressora HP parou de imprimir ontem."),
            Message(previous, 2, "assistant", "Reinstalei o driver da impressora e voltou."),
            Message(previous, 3, "tool", "{\"tool\":\"printer\",\"impressora\":true}"),
            Message(previous, 4, "user", "Nada a ver com o assunto"),
            Message(current, 1, "user", "A impressora travou de novo hoje."),
            Message(otherAgentSession, 1, "user", "Impressora de outro cliente"),
            Message(deletedSession, 1, "user", "Impressora de sessão apagada"));
        await db.SaveChangesAsync();

        var hits = await new AiChatMessageRepository(db)
            .SearchByAgentAsync(agentId, "IMPRESSORA", limit: 10, excludeSessionId: current);

        Assert.That(hits, Has.Count.EqualTo(2),
            "somente user/assistant das conversas anteriores do MESMO agente");
        Assert.That(hits.All(m => m.SessionId == previous), Is.True);
        Assert.That(hits.Any(m => m.Role == "tool"), Is.False,
            "payload de tool não é memória de conversa");
    }

    [Test]
    public async Task SearchByAgent_TrimsTruncatesAndClamps()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var session = Guid.NewGuid();
        db.Add(Session(session, agentId));
        await db.SaveChangesAsync();

        var repo = new AiChatMessageRepository(db);

        Assert.That(await repo.SearchByAgentAsync(agentId, "   ", 5), Is.Empty,
            "query vazia/só espaços não pode varrer o histórico");
        Assert.That(await repo.SearchByAgentAsync(agentId, new string('a', 500), 999), Is.Empty,
            "query longa é truncada e o limite é normalizado sem estourar");
    }

    private static AiChatSession Session(Guid id, Guid agentId, DateTime? deletedAt = null) => new()
    {
        Id = id,
        AgentId = agentId,
        SiteId = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(180),
        DeletedAt = deletedAt,
        CreatedByIp = "127.0.0.1",
    };

    private static AiChatMessage Message(Guid sessionId, int seq, string role, string content) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        SequenceNumber = seq,
        Role = role,
        Content = content,
        CreatedAt = DateTime.UtcNow.AddMinutes(-seq),
    };

    private static MemorySearchTestDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ai-chat-memory-{Guid.NewGuid():N}")
            .Options;
        return new MemorySearchTestDbContext(options);
    }

    /// <summary>Contexto restrito a AiChatSession/AiChatMessage (InMemory).</summary>
    private sealed class MemorySearchTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(AiChatSession).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(AiChatSession) && type != typeof(AiChatMessage)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AiChatSession>(entity =>
            {
                entity.HasKey(s => s.Id);
                entity.Ignore(s => s.Agent);
            });
            modelBuilder.Entity<AiChatMessage>(entity => entity.HasKey(m => m.Id));
        }
    }
}
