using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services.Ai;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Fluxos de decisão da triagem por IA: IA indisponível, resposta inválida,
/// aplicação no modo automático, modo assistido e departamento sem candidatos.
/// Toda decisão precisa ficar auditável e o chamado nunca pode quebrar.
/// </summary>
public class AiTicketTriageServiceTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("ai-triage-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new TriageTestDbContext(options);
    }

    private static async Task<(DiscoveryDbContext Db, Ticket Ticket, Department Department, User Top, User Other)> SeedAsync(
        AiAssignmentMode mode = AiAssignmentMode.AutoAssign, bool accepts = true)
    {
        var db = CreateDb();
        var now = DateTime.UtcNow;

        var top = new User { Id = Guid.NewGuid(), Login = "ana", Email = "ana@x.com", FullName = "Ana Souza", IsActive = true };
        var other = new User { Id = Guid.NewGuid(), Login = "bruno", Email = "bruno@x.com", FullName = "Bruno Lima", IsActive = true };
        db.Users.AddRange(top, other);

        var department = new Department
        {
            Id = Guid.NewGuid(),
            Name = "Suporte",
            AssignmentStrategy = (int)TicketAssignmentStrategy.AiTriage,
            AiAssignmentMode = (int)mode,
            AiAssignmentFallbackStrategy = (int)TicketAssignmentStrategy.LeastOpenTickets,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Departments.Add(department);

        db.DepartmentMembers.AddRange(
            new DepartmentMember
            {
                Id = Guid.NewGuid(),
                DepartmentId = department.Id,
                UserId = top.Id,
                IsActive = true,
                SkillTagsJson = JsonSerializer.Serialize(new[] { "rede" }),
                SkillLevel = 5,
                AcceptsAiAssignment = accepts,
                CreatedAt = now
            },
            new DepartmentMember
            {
                Id = Guid.NewGuid(),
                DepartmentId = department.Id,
                UserId = other.Id,
                IsActive = true,
                SkillTagsJson = JsonSerializer.Serialize(new[] { "excel" }),
                SkillLevel = 1,
                AcceptsAiAssignment = accepts,
                CreatedAt = now.AddMinutes(1)
            });

        var clientId = Guid.NewGuid();
        db.Sites.Add(new Site
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = "Matriz",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            WorkflowStateId = Guid.NewGuid(),
            DepartmentId = department.Id,
            Title = "VPN nao conecta",
            Description = "A VPN da filial cai ao conectar no servidor de arquivos",
            Priority = TicketPriority.High,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        return (db, ticket, department, top, other);
    }

    private static AiTicketTriageService BuildService(
        DiscoveryDbContext db, IAiChatService aiChat,
        FakeQueue? queue = null, BackgroundProcessingSettings? settings = null)
        => new(
            db,
            queue ?? new FakeQueue(),
            new StaticMetrics(),
            new NoopAffinity(),
            new TicketDifficultyAssessor(),
            new TicketAssignmentService(db, new DepartmentTeamResolver(db)),
            aiChat,
            new ActivityLogService(new TicketActivityLogRepository(db), NullLogger<ActivityLogService>.Instance),
            new NoopNotifications(),
            new FakeTokenBudget(),
            new DepartmentTeamResolver(db),
            new FakeProcessingConfig(settings ?? new BackgroundProcessingSettings()),
            NullLogger<AiTicketTriageService>.Instance);

    [Test]
    public async Task Apply_AssignsModelChoice_RecordsDecisionAndActivity()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9));
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.Applied, Is.True);
        Assert.That(result.AssignedUserId, Is.EqualTo(top.Id));

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.EqualTo(top.Id));

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.Ai));
        Assert.That(decision.ChosenUserId, Is.EqualTo(top.Id));
        Assert.That(decision.Applied, Is.True);
        // Auditoria do orçamento de tokens (modelo + teto do departamento).
        Assert.That(decision.MaxOutputTokens, Is.GreaterThan(0));
        Assert.That(decision.PromptChars, Is.GreaterThan(0));

        var activity = await db.TicketActivityLogs.AsNoTracking().SingleAsync();
        Assert.That(activity.Type, Is.EqualTo(TicketActivityType.AiAssigned));
    }

    [Test]
    public async Task Recommend_InSuggestMode_DoesNotAssign()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.Suggest);
        await using var _db = db;

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9));
        var result = await service.RecommendAsync(ticket.Id);

        Assert.That(result.Applied, Is.False);
        Assert.That(result.AssignedUserId, Is.Null);

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.Null);

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.Applied, Is.False);
        Assert.That(decision.NotAppliedReason, Is.EqualTo("suggest_only"));

        var activity = await db.TicketActivityLogs.AsNoTracking().SingleAsync();
        Assert.That(activity.Type, Is.EqualTo(TicketActivityType.AiAssignmentSuggested));
    }

    [Test]
    public async Task AiUnavailable_AppliesConfiguredFallbackOnApply()
    {
        var (db, ticket, department, top, other) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        // Cursor do round-robin apontando para o top: o próximo é o other, ou
        // seja, o fallback configurado difere da escolha por score (top).
        var tracked = await db.Departments.FirstAsync(d => d.Id == department.Id);
        tracked.AiAssignmentFallbackStrategy = (int)TicketAssignmentStrategy.RoundRobin;
        tracked.RoundRobinLastUserId = top.Id;
        await db.SaveChangesAsync();

        var service = BuildService(db, new ThrowingAiChat());
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.Applied, Is.True);
        Assert.That(result.AssignedUserId, Is.EqualTo(other.Id));

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.AiUnavailable));
        Assert.That(decision.ChosenUserId, Is.EqualTo(other.Id));
        Assert.That(decision.Rationale, Does.Contain("fallback configurada"));
    }

    [Test]
    public async Task ModelChoseUnknownUser_FallsBackToHighestScore()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var service = BuildService(db, new FakeAiChat(Guid.NewGuid(), 0.99));
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.AssignedUserId, Is.EqualTo(top.Id));

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.FallbackScore));
    }

    [Test]
    public async Task LowModelConfidence_UsesHighestScore()
    {
        var (db, ticket, _, top, other) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var service = BuildService(db, new FakeAiChat(other.Id, 0.1));
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.AssignedUserId, Is.EqualTo(top.Id));

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.FallbackScore));
    }

    [Test]
    public async Task NoEligibleMembers_DoesNotAssignAndRecordsReason()
    {
        var (db, ticket, _, _, _) = await SeedAsync(AiAssignmentMode.AutoAssign, accepts: false);
        await using var _db = db;

        var service = BuildService(db, new ThrowingAiChat());
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.Applied, Is.False);

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.NoCandidates));
        Assert.That(decision.ChosenUserId, Is.Null);
    }

    [Test]
    public async Task Apply_DoesNotOverwriteManualAssignment()
    {
        var (db, ticket, _, _, other) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var stored = await db.Tickets.FirstAsync(t => t.Id == ticket.Id);
        stored.AssignedToUserId = other.Id;
        await db.SaveChangesAsync();

        var service = BuildService(db, new FakeAiChat(other.Id, 0.9));
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.Applied, Is.False);
        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.NotAppliedReason, Is.EqualTo("already_assigned"));
    }

    [Test]
    public async Task TicketWithoutSite_UsesClientSiteForAiSettings()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        Assert.That(ticket.SiteId, Is.Null, "o cenário precisa de um chamado sem site");

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9));
        var result = await service.ApplyAsync(ticket.Id);

        Assert.That(result.Decision.StrategySource, Is.EqualTo(AiAssignmentDecisionSource.Ai),
            "sem o site do cliente a triagem cairia em ai_unavailable");
    }

    [Test]
    public async Task Preview_DoesNotWriteActivityOrNotification()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.Suggest);
        await using var _db = db;

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9));
        var result = await service.PreviewAsync(ticket.Id);

        Assert.That(result.Applied, Is.False);
        Assert.That(await db.TicketActivityLogs.AsNoTracking().CountAsync(), Is.EqualTo(0));

        var decision = await db.TicketAssignmentDecisions.AsNoTracking().SingleAsync();
        Assert.That(decision.NotAppliedReason, Is.EqualTo("preview"));
    }

    [Test]
    public async Task ProcessDueAsync_DoesNotAssignInSuggestMode()
    {
        var (db, ticket, _, _, _) = await SeedAsync(AiAssignmentMode.Suggest);
        await using var _db = db;

        var service = BuildService(db, new ThrowingAiChat());
        var result = await service.ProcessDueAsync();

        Assert.That(result.Swept, Is.EqualTo(0));
        Assert.That(result.Triaged, Is.EqualTo(0));

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.Null,
            "no modo assistido o chamado espera confirmação humana");
    }

    [Test]
    public async Task ProcessDueAsync_SweepsUnassignedInAutoMode()
    {
        var (db, ticket, _, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        // RetryAfterMinutes = 0: a varredura de segurança age no mesmo ciclo.
        var settings = new BackgroundProcessingSettings();
        settings.Triage.RetryAfterMinutes = 0;

        var service = BuildService(db, new ThrowingAiChat(), settings: settings);
        var result = await service.ProcessDueAsync();

        Assert.That(result.Swept, Is.EqualTo(1));

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.EqualTo(top.Id));
    }

    [Test]
    public async Task ProcessDueAsync_ProcessesQueuedItemForDueScope()
    {
        var (db, ticket, department, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var queue = new FakeQueue();
        queue.Pending.Add(new AiAssignmentQueueItem
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            DepartmentId = department.Id,
            Status = AiAssignmentQueueStatus.Pending,
            Attempts = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9), queue);
        var result = await service.ProcessDueAsync();

        Assert.That(result.Triaged, Is.EqualTo(1));
        Assert.That(result.ScopesProcessed, Is.EqualTo(1));
        Assert.That(queue.MarkedDone, Has.Count.EqualTo(1));

        var stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);
        Assert.That(stored.AssignedToUserId, Is.EqualTo(top.Id));
    }

    [Test]
    public async Task ProcessDueAsync_SkipsScopeBeforeItsInterval()
    {
        var (db, ticket, department, top, _) = await SeedAsync(AiAssignmentMode.AutoAssign);
        await using var _db = db;

        var queue = new FakeQueue();
        queue.Pending.Add(new AiAssignmentQueueItem
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            DepartmentId = department.Id,
            Status = AiAssignmentQueueStatus.Pending,
            Attempts = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var service = BuildService(db, new FakeAiChat(top.Id, 0.9), queue);

        await service.ProcessDueAsync();

        // Segunda execução imediata: o escopo ainda não venceu (IntervalSeconds).
        queue.Pending.Add(new AiAssignmentQueueItem
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            DepartmentId = department.Id,
            Status = AiAssignmentQueueStatus.Pending,
            Attempts = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var second = await service.ProcessDueAsync();

        Assert.That(second.ScopesProcessed, Is.EqualTo(0), "escopo não venceu novamente");
        Assert.That(queue.MarkedDone, Has.Count.EqualTo(1), "nada novo foi processado");
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class StaticMetrics : ITechnicianMetricsService
    {
        public Task<TechnicianMetricsDto> GetMetricsAsync(
            Guid userId, Guid? clientScope = null, CancellationToken ct = default)
            => Task.FromResult(MetricsFor(userId));

        public Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
            IReadOnlyCollection<Guid> userIds, Guid? clientScope = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TechnicianMetricsDto>>(userIds.Select(MetricsFor).ToList());

        public Task<int> RefreshSnapshotsAsync(
            IReadOnlyCollection<Guid>? userIds = null, Guid? departmentId = null, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<MetricsRefreshResult> RefreshDueAsync(CancellationToken ct = default)
            => Task.FromResult(new MetricsRefreshResult(0, 0, 0, new Dictionary<Guid, int>(), 0));

        public Task<MetricsBackfillProgress> RefreshForcedAsync(
            Guid? clientId, DateTime sessionStartUtc, int maxUsers, CancellationToken ct = default)
            => Task.FromResult(new MetricsBackfillProgress(0, 0, false));

        public Task<int> PurgeOrphanSnapshotsAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        private static TechnicianMetricsDto MetricsFor(Guid userId)
            => new(userId, 90, 10, 8, 1, 20, 60, 90, 0, 0, 4.8, 10, null, [], [], DateTime.UtcNow);
    }

    private sealed class NoopAffinity : ITechnicianAffinityRepository
    {
        public Task<IReadOnlyList<TechnicianAffinityHit>> FindSimilarResolvedAsync(
            string query, Guid? clientId, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TechnicianAffinityHit>>([]);
    }

    private sealed class FakeQueue : IAiAssignmentQueueRepository
    {
        public List<AiAssignmentQueueItem> Pending { get; } = [];
        public List<Guid> MarkedDone { get; } = [];
        public List<(Guid Id, string Reason)> MarkedSkipped { get; } = [];

        public Task EnqueueAsync(Guid ticketId, Guid departmentId, string? reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AiAssignmentQueueItem>>([]);

        // Os departamentos dos testes são globais (ClientId = null) => escopo Guid.Empty,
        // o mesmo que a varredura de segurança calcula.
        public Task<IReadOnlyList<Guid>> ListPendingClientScopesAsync(int maxScopes, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(
                Pending.Select(_ => Guid.Empty).Distinct().ToList());

        public Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchForClientAsync(
            Guid clientId, int limit, CancellationToken ct = default)
        {
            var claimed = Pending.Take(limit).ToList();
            Pending.RemoveRange(0, claimed.Count);
            return Task.FromResult<IReadOnlyList<AiAssignmentQueueItem>>(claimed);
        }

        public Task MarkDoneAsync(Guid id, CancellationToken ct = default)
        {
            MarkedDone.Add(id);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkSkippedAsync(Guid id, string reason, CancellationToken ct = default)
        {
            MarkedSkipped.Add((id, reason));
            return Task.CompletedTask;
        }

        public Task<int> CountOutstandingAsync(CancellationToken ct = default) => Task.FromResult(Pending.Count);
    }

    private sealed class FakeProcessingConfig(BackgroundProcessingSettings settings) : IConfigurationResolver
    {
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default)
            => Task.FromResult(settings);

        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }

    private sealed class FakeTokenBudget : IAiTokenBudgetResolver
    {
        public Task<AiTokenBudgetDto> ResolveForSiteAsync(
            Guid siteId, int requested, int? departmentCap = null, CancellationToken ct = default)
        {
            var (cap, source) = AiTokenLimits.ResolveOutputCap(requested, 16384, departmentCap, "gpt-4o-mini");
            return Task.FromResult(new AiTokenBudgetDto(
                "gpt-4o-mini", 128000, 16384, cap, 8000, source));
        }

        public Task<AiTokenBudgetDto> ResolveAsync(
            Guid siteId, Guid? clientId, Discovery.Core.ValueObjects.AIIntegrationSettings settings, int requested,
            int? departmentCap = null, CancellationToken ct = default)
            => ResolveForSiteAsync(siteId, requested, departmentCap, ct);
    }

    private sealed class NoopNotifications : INotificationService
    {
        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppNotification
            {
                Id = Guid.NewGuid(),
                EventType = request.EventType,
                Topic = request.Topic,
                Title = request.Title,
                Message = request.Message
            });

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(false);
    }

    private sealed class ThrowingAiChat : FakeAiChatBase
    {
        public override Task<LlmResponse> ProcessTicketPromptJsonAsync(
            string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
            string? responseFormat, Guid? departmentId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("IA não configurada.");
    }

    private sealed class FakeAiChat(Guid? chosenUserId, double confidence) : FakeAiChatBase
    {
        public override Task<LlmResponse> ProcessTicketPromptJsonAsync(
            string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature,
            string? responseFormat, Guid? departmentId = null, CancellationToken ct = default)
        {
            var content = "{\"chosenUserId\":\"" + chosenUserId + "\",\"confidence\":" +
                confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"difficulty\":4,\"tags\":[\"vpn\"],\"rationale\":\"melhor afinidade\"}";
            return Task.FromResult(new LlmResponse(content, 100, "fake-model"));
        }
    }

    private abstract class FakeAiChatBase : IAiChatService
    {
        public Task<AgentChatSyncResponse> ProcessSyncAsync(Guid agentId, string message, Guid? sessionId, string? createdByIp = null, int? requestMaxTokens = null, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> ProcessAsyncAsync(Guid agentId, string message, Guid? sessionId, int? requestMaxTokens = null, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AgentChatJobStatus> GetJobStatusAsync(Guid jobId, Guid agentId, CancellationToken ct)
            => throw new NotSupportedException();

        public virtual Task<LlmResponse> ProcessTicketPromptAsync(string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature, Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public abstract Task<LlmResponse> ProcessTicketPromptJsonAsync(string systemPrompt, string userMessage, Guid siteId, int maxTokens, double temperature, string? responseFormat, Guid? departmentId = null, CancellationToken ct = default);

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

    private sealed class TriageTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(User), typeof(Client), typeof(Site), typeof(Department), typeof(DepartmentMember),
                typeof(Ticket), typeof(TicketActivityLog), typeof(TicketAssignmentDecision),
                typeof(ProcessingScopeState)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => { e.HasKey(c => c.Id); e.Property(c => c.Name).IsRequired(); });
            modelBuilder.Entity<User>(e => e.HasKey(u => u.Id));
            modelBuilder.Entity<Site>(e => e.HasKey(s => s.Id));
            modelBuilder.Entity<Department>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<DepartmentMember>(e => e.HasKey(m => m.Id));
            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketActivityLog>(e => e.HasKey(l => l.Id));
            modelBuilder.Entity<TicketAssignmentDecision>(e => e.HasKey(d => d.Id));
            modelBuilder.Entity<ProcessingScopeState>(e => e.HasKey(s => s.Id));
        }
    }
}
