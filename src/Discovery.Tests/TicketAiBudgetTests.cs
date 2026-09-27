using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.TicketAi;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Automação do orçamento de tokens nos fluxos de IA de chamado: o handler não
/// pede mais do que o modelo suporta, trunca o prompt pela janela de contexto e
/// resolve o site de chamados sem SiteId.
/// </summary>
public class TicketAiBudgetTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("ticket-ai-budget-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new TicketAiTestDbContext(options);
    }

    private static Ticket NewTicket(Guid clientId, string description)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            WorkflowStateId = Guid.NewGuid(),
            Title = "Chamado longo",
            Description = description,
            Priority = TicketPriority.Medium,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public async Task Handler_UsesModelBudget_TruncatesPrompt_AndResolvesClientSite()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Matriz",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Sites.Add(site);

        var ticket = NewTicket(clientId, new string('x', 5000));
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var chat = new CapturingAiChat();
        var handler = new TicketAiSummarizeCommandHandler(
            new FakeTicketRepository(ticket), chat, db, new FixedBudget(700, 200));

        var result = await handler.Handle(new TicketAiSummarizeCommand(ticket.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(chat.LastSiteId, Is.EqualTo(site.Id),
            "chamado sem SiteId usa um site do mesmo cliente");
        Assert.That(chat.LastMaxTokens, Is.EqualTo(700),
            "o pedido de 1024 é limitado pelo orçamento do modelo");
        Assert.That(chat.LastMessage, Does.EndWith("..."), "prompt truncado pelo orçamento");
        Assert.That(chat.LastMessage.Length, Is.LessThanOrEqualTo(203));
    }

    [Test]
    public async Task Handler_WithoutUsableSite_FailsWithClearMessage()
    {
        await using var db = CreateDb();
        var ticket = NewTicket(Guid.NewGuid(), "sem site");
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var handler = new TicketAiSummarizeCommandHandler(
            new FakeTicketRepository(ticket), new CapturingAiChat(), db, new FixedBudget(700, 200));

        var result = await handler.Handle(new TicketAiSummarizeCommand(ticket.Id), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Message, Does.Contain("sem site"));
    }

    [Test]
    public async Task Handler_WhenUsageLimitBlocks_ReturnsFriendlyError()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.Sites.Add(new Site
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Matriz",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var ticket = NewTicket(clientId, "curto");
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var handler = new TicketAiSummarizeCommandHandler(
            new FakeTicketRepository(ticket), new CapturingAiChat(), db, new BlockedBudget());

        var result = await handler.Handle(new TicketAiSummarizeCommand(ticket.Id), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Message, Does.Contain("Limite de uso de IA"));
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FixedBudget(int maxOutputTokens, int maxPromptChars) : IAiTokenBudgetResolver
    {
        public Task<AiTokenBudgetDto> ResolveForSiteAsync(
            Guid siteId, int requested, int? departmentCap = null, CancellationToken ct = default)
            => Task.FromResult(new AiTokenBudgetDto(
                "gpt-4o-mini", 128000, 16384, maxOutputTokens, maxPromptChars, "catalog"));

        public Task<AiTokenBudgetDto> ResolveAsync(
            Guid siteId, Guid? clientId, Core.ValueObjects.AIIntegrationSettings settings, int requested,
            int? departmentCap = null, CancellationToken ct = default)
            => ResolveForSiteAsync(siteId, requested, departmentCap, ct);
    }

    private sealed class BlockedBudget : IAiTokenBudgetResolver
    {
        public Task<AiTokenBudgetDto> ResolveForSiteAsync(
            Guid siteId, int requested, int? departmentCap = null, CancellationToken ct = default)
            => throw new AiUsageLimitException(AiUsageLimitReason.RateOrBudget, "rate limit ou budget diário de IA atingido");

        public Task<AiTokenBudgetDto> ResolveAsync(
            Guid siteId, Guid? clientId, Core.ValueObjects.AIIntegrationSettings settings, int requested,
            int? departmentCap = null, CancellationToken ct = default)
            => ResolveForSiteAsync(siteId, requested, departmentCap, ct);
    }

    private sealed class CapturingAiChat : IAiChatService
    {
        public Guid LastSiteId { get; private set; }
        public int LastMaxTokens { get; private set; }
        public string LastMessage { get; private set; } = string.Empty;

        public Task<LlmResponse> ProcessTicketPromptAsync(
            string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
            Guid? departmentId = null, CancellationToken ct = default)
        {
            LastSiteId = siteId;
            LastMaxTokens = maxTokens;
            LastMessage = userMessage;
            return Task.FromResult(new LlmResponse("resumo", 42, "fake-model"));
        }

        public Task<LlmResponse> ProcessTicketPromptJsonAsync(
            string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
            string? responseFormat, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentChatSyncResponse> ProcessSyncAsync(Guid agentId, string message, Guid? sessionId, string? createdByIp = null, int? requestMaxTokens = null, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> ProcessAsyncAsync(Guid agentId, string message, Guid? sessionId, int? requestMaxTokens = null, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentChatJobStatus> GetJobStatusAsync(Guid jobId, Guid agentId, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AiChatStreamChunk> StreamAsync(Guid agentId, string message, Guid? sessionId, Guid? departmentId = null, string? systemNote = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task RegisterAgentToolsAsync(Guid agentId, Guid siteId, List<AgentToolRegistration> tools, CancellationToken ct = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<AiChatStreamChunk> StreamMultiRoundAsync(Guid agentId, string? message, Guid? sessionId, List<ToolResultItem>? toolResults, Guid? departmentId = null, string? systemNote = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeTicketRepository(Ticket ticket) : ITicketRepository
    {
        public Task<Ticket?> GetByIdAsync(Guid id) => Task.FromResult<Ticket?>(ticket.Id == id ? ticket : null);

        public Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null) => throw new NotSupportedException();
        public Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null) => throw new NotSupportedException();
        public Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter) => throw new NotSupportedException();
        public Task<IReadOnlyList<Ticket>> GetAllPageAsync(TicketFilterQuery filter) => throw new NotSupportedException();
        public Task<Ticket> CreateAsync(Ticket value) => throw new NotSupportedException();
        public Task UpdateAsync(Ticket value) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId) => throw new NotSupportedException();
        public Task<IReadOnlyList<TicketComment>> GetCommentsPageAsync(Guid ticketId, string? cursor, int limit) => throw new NotSupportedException();
        public Task<TicketComment> AddCommentAsync(TicketComment comment) => throw new NotSupportedException();
        public Task<List<Ticket>> GetOpenTicketsWithSlaAsync(int limit = 2000) => throw new NotSupportedException();
        public Task<List<Ticket>> GetOpenWithoutProfileByDepartmentAsync(Guid departmentId, int limit = 500) => throw new NotSupportedException();
        public Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds) => throw new NotSupportedException();
        public Task UpdateWorkflowStateWithSlaHoldAsync(Guid id, Guid workflowStateId, DateTime? closedAt, DateTime? slaHoldStartedAt, int slaPausedSeconds) => throw new NotSupportedException();
        public Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt) => throw new NotSupportedException();
        public Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since) => throw new NotSupportedException();
        public Task<TicketKpiResult> GetKpiAsync(TicketFilterQuery filter) => throw new NotSupportedException();
    }

    private sealed class TicketAiTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Client), typeof(Site), typeof(Ticket) };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<Site>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
        }
    }
}
