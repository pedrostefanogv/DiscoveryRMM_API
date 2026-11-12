using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
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

    // ── B18: a fronteira do histórico nunca pode cair no meio do grupo ───────

    private static AiChatMessage ToolMessage(Guid sessionId, int seq, string toolCallId, string content) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        SequenceNumber = seq,
        Role = "tool",
        Content = content,
        ToolCallId = toolCallId,
        ToolName = "get_pending_updates",
        CreatedAt = DateTime.UtcNow.AddMinutes(-seq),
    };

    private static AiChatMessage CallsMessage(Guid sessionId, int seq, params string[] ids) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        SequenceNumber = seq,
        Role = "assistant",
        Content = "consultando",
        ToolCallsJson = "[" + string.Join(",", Array.ConvertAll(ids,
            id => "{\"id\":\"" + id + "\",\"name\":\"get_pending_updates\",\"arguments\":\"{}\"}")) + "]",
        CreatedAt = DateTime.UtcNow.AddMinutes(-seq),
    };

    /// <summary>
    /// CENÁRIO DO INCIDENTE (sessão 01a1cd68): a declaração de 8 calls está na
    /// seq 4 e o corte em 10 mensagens começava na seq 7 — um role='tool'
    /// respondendo call_..._4. A fronteira precisa RECUAR até a declaração para
    /// o payload não começar com function_call_output órfão.
    /// </summary>
    [Test]
    public async Task GetRecentBySession_BacksUpBoundaryToTheDeclaringAssistant()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var session = Guid.NewGuid();
        db.Add(Session(session, agentId));

        var ids = Enumerable.Range(2, 8).Select(n => $"call_72e873b9f3732521_{n}").ToArray();
        db.Add(CallsMessage(session, 4, ids));
        for (var i = 0; i < ids.Length; i++)
            db.Add(ToolMessage(session, 5 + i, ids[i], "resultado"));
        db.Add(Message(session, 13, "assistant", "resumo"));
        db.Add(Message(session, 14, "user", "instale o foxit"));
        await db.SaveChangesAsync();

        var repo = new AiChatMessageRepository(db);
        var window = await repo.GetRecentBySessionAsync(session, 10);

        Assert.That(window, Has.Count.LessThanOrEqualTo(10), "maxHistoryMessages continua sendo o teto");
        Assert.That(window[0].SequenceNumber, Is.EqualTo(4),
            "a fronteira recua até o assistant que declara os calls da janela");
        Assert.That(window.Select(m => m.SequenceNumber), Is.EqualTo(new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }),
            "o teto corta o FIM da janela, nunca o meio do grupo");

        var payload = AiChatToolOrchestrator.BuildLlmMessagesFromHistory(window);
        Assert.That(payload.Any(m => m.Role == "tool"), Is.True, "as respostas com declaração na janela são preservadas");
        Assert.That(payload[0].ToolCalls, Is.Not.Null, "o assistant declarante precisa estar na janela");
        Assert.That(payload[0].ToolCalls!.Count, Is.EqualTo(8));
    }

    /// <summary>
    /// Sessão com mais mensagens que o limite: o teto de maxHistoryMessages não
    /// pode ser estourado pelo alinhamento (custo de prompt).
    /// </summary>
    [Test]
    public async Task GetRecentBySession_NeverExceedsTheLimit()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var session = Guid.NewGuid();
        db.Add(Session(session, agentId));
        db.Add(CallsMessage(session, 1, "_1", "_2"));
        db.Add(ToolMessage(session, 2, "_1", "r1"));
        db.Add(ToolMessage(session, 3, "_2", "r2"));
        for (var seq = 4; seq <= 40; seq++)
            db.Add(Message(session, seq, seq % 2 == 0 ? "user" : "assistant", $"m{seq}"));
        await db.SaveChangesAsync();

        var repo = new AiChatMessageRepository(db);

        Assert.That(await repo.GetRecentBySessionAsync(session, 10), Has.Count.LessThanOrEqualTo(10));
        Assert.That(await repo.GetRecentBySessionAsync(session, 1), Has.Count.LessThanOrEqualTo(1));
    }

    /// <summary>Sessão curta (nunca truncada) continua chegando intacta.</summary>
    [Test]
    public async Task GetRecentBySession_ShortSessionIsUntouched()
    {
        await using var db = NewDb();
        var agentId = Guid.NewGuid();
        var session = Guid.NewGuid();
        db.Add(Session(session, agentId));
        db.Add(Message(session, 1, "user", "u1"));
        db.Add(CallsMessage(session, 2, "_1"));
        db.Add(ToolMessage(session, 3, "_1", "r1"));
        db.Add(Message(session, 4, "assistant", "pronto"));
        await db.SaveChangesAsync();

        var window = await new AiChatMessageRepository(db).GetRecentBySessionAsync(session, 10);

        Assert.That(window.Select(m => m.SequenceNumber), Is.EqualTo(new[] { 1, 2, 3, 4 }));
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
