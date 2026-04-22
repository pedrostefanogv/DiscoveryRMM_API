using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class AutoTicketServiceTests
{
    [Test]
    public async Task EvaluateAsync_ShouldReopenClosedTicket_WhenInsideConfiguredReopenWindow()
    {
        await using var db = CreateOrchestratorDb();
        var monitoringEvent = BuildMonitoringEvent();
        var decision = BuildCreateDecision();
        var closedTicket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = monitoringEvent.ClientId,
            SiteId = monitoringEvent.SiteId,
            AgentId = monitoringEvent.AgentId,
            DepartmentId = decision.Rule!.TargetDepartmentId,
            WorkflowProfileId = decision.Rule.TargetWorkflowProfileId,
            WorkflowStateId = Guid.NewGuid(),
            Category = decision.Rule.TargetCategory,
            Title = "Closed Ticket",
            Description = "Closed by test",
            ClosedAt = DateTime.UtcNow.AddMinutes(-10),
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var executionRepository = new FakeAutoTicketRuleExecutionRepository
        {
            ReopenableClosedTicketId = closedTicket.Id
        };
        var dedupService = new FakeAutoTicketDedupService();
        var alertToTicketService = new FakeAlertToTicketService();
        var ticketRepository = new FakeTicketRepository(closedTicket);
        var workflowRepository = new FakeWorkflowRepository();
        var activityLogService = new FakeActivityLogService();
        var normalizationService = new MonitoringEventNormalizationService();
        var orchestrator = new AutoTicketOrchestratorService(
            new FakeAutoTicketRuleEngineService(decision),
            dedupService,
            executionRepository,
            alertToTicketService,
            ticketRepository,
            workflowRepository,
            activityLogService,
            normalizationService,
            new DedupFingerprintService(normalizationService),
            db,
            Options.Create(new AutoTicketOptions { Enabled = true, ShadowMode = false, ReopenWindowMinutes = 30 }),
            NullLogger<AutoTicketOrchestratorService>.Instance);

        var result = await orchestrator.EvaluateAsync(monitoringEvent);
        var reopenedTicket = await ticketRepository.GetByIdAsync(closedTicket.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.Deduped));
            Assert.That(result.CreatedTicketId, Is.EqualTo(closedTicket.Id));
            Assert.That(alertToTicketService.Requests, Has.Count.EqualTo(0));
            Assert.That(executionRepository.ReopenLookupCalls, Has.Count.EqualTo(1));
            Assert.That(activityLogService.Logs.Any(log => log.Type == TicketActivityType.Reopened && log.TicketId == closedTicket.Id), Is.True);
            Assert.That(reopenedTicket, Is.Not.Null);
            Assert.That(reopenedTicket!.ClosedAt, Is.Null);
            Assert.That(reopenedTicket.WorkflowStateId, Is.EqualTo(workflowRepository.InitialStateId));
        });
    }

    [Test]
    public async Task EvaluateAsync_ShouldReuseExistingOpenTicket_WhenSameAlertTypeAlreadyHasOpenTicket()
    {
        await using var db = CreateOrchestratorDb();
        var expectedTicketId = Guid.NewGuid();
        var monitoringEvent = BuildMonitoringEvent();
        var decision = BuildCreateDecision();
        var executionRepository = new FakeAutoTicketRuleExecutionRepository
        {
            ReusableOpenTicketId = expectedTicketId
        };
        var dedupService = new FakeAutoTicketDedupService();
        var alertToTicketService = new FakeAlertToTicketService();
        var ticketRepository = new FakeTicketRepository();
        var workflowRepository = new FakeWorkflowRepository();
        var activityLogService = new FakeActivityLogService();
        var normalizationService = new MonitoringEventNormalizationService();
        var orchestrator = new AutoTicketOrchestratorService(
            new FakeAutoTicketRuleEngineService(decision),
            dedupService,
            executionRepository,
            alertToTicketService,
            ticketRepository,
            workflowRepository,
            activityLogService,
            normalizationService,
            new DedupFingerprintService(normalizationService),
            db,
            Options.Create(new AutoTicketOptions { Enabled = true, ShadowMode = false }),
            NullLogger<AutoTicketOrchestratorService>.Instance);

        var result = await orchestrator.EvaluateAsync(monitoringEvent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.Deduped));
            Assert.That(result.CreatedTicketId, Is.EqualTo(expectedTicketId));
            Assert.That(result.DedupHit, Is.True);
            Assert.That(alertToTicketService.Requests, Has.Count.EqualTo(0));
            Assert.That(executionRepository.LookupCalls, Has.Count.EqualTo(1));
            Assert.That(executionRepository.ReopenLookupCalls, Has.Count.EqualTo(0));
            Assert.That(dedupService.RegisteredTicketIds, Has.Count.EqualTo(1));
            Assert.That(dedupService.RegisteredTicketIds[0], Is.EqualTo(expectedTicketId));
        });
    }

    [Test]
    public async Task EvaluateAsync_ShouldCreateTicket_WhenNoReusableOpenTicketExists()
    {
        await using var db = CreateOrchestratorDb();
        var monitoringEvent = BuildMonitoringEvent();
        var decision = BuildCreateDecision();
        var executionRepository = new FakeAutoTicketRuleExecutionRepository();
        var dedupService = new FakeAutoTicketDedupService();
        var alertToTicketService = new FakeAlertToTicketService
        {
            TicketToReturn = new Ticket
            {
                Id = Guid.NewGuid(),
                ClientId = monitoringEvent.ClientId,
                AgentId = monitoringEvent.AgentId,
                WorkflowStateId = Guid.NewGuid(),
                Title = "Created Ticket",
                Description = "Created by test",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };
        var ticketRepository = new FakeTicketRepository();
        var workflowRepository = new FakeWorkflowRepository();
        var activityLogService = new FakeActivityLogService();
        var normalizationService = new MonitoringEventNormalizationService();
        var orchestrator = new AutoTicketOrchestratorService(
            new FakeAutoTicketRuleEngineService(decision),
            dedupService,
            executionRepository,
            alertToTicketService,
            ticketRepository,
            workflowRepository,
            activityLogService,
            normalizationService,
            new DedupFingerprintService(normalizationService),
            db,
            Options.Create(new AutoTicketOptions { Enabled = true, ShadowMode = false }),
            NullLogger<AutoTicketOrchestratorService>.Instance);

        var result = await orchestrator.EvaluateAsync(monitoringEvent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.Created));
            Assert.That(result.CreatedTicketId, Is.EqualTo(alertToTicketService.TicketToReturn!.Id));
            Assert.That(alertToTicketService.Requests, Has.Count.EqualTo(1));
            Assert.That(executionRepository.LookupCalls, Has.Count.EqualTo(1));
            Assert.That(executionRepository.ReopenLookupCalls, Has.Count.EqualTo(0));
            Assert.That(dedupService.RegisteredTicketIds, Has.Count.EqualTo(1));
            Assert.That(dedupService.RegisteredTicketIds[0], Is.EqualTo(alertToTicketService.TicketToReturn!.Id));
        });
    }

    [Test]
    public async Task EvaluateAsync_ShouldRateLimit_WhenHourlyCapIsReached()
    {
        await using var db = CreateOrchestratorDb();
        var monitoringEvent = BuildMonitoringEvent();
        var decision = BuildCreateDecision();
        var executionRepository = new FakeAutoTicketRuleExecutionRepository
        {
            CreatedCountForClientAlert = 3
        };
        var dedupService = new FakeAutoTicketDedupService();
        var alertToTicketService = new FakeAlertToTicketService();
        var ticketRepository = new FakeTicketRepository();
        var workflowRepository = new FakeWorkflowRepository();
        var activityLogService = new FakeActivityLogService();
        var normalizationService = new MonitoringEventNormalizationService();
        var orchestrator = new AutoTicketOrchestratorService(
            new FakeAutoTicketRuleEngineService(decision),
            dedupService,
            executionRepository,
            alertToTicketService,
            ticketRepository,
            workflowRepository,
            activityLogService,
            normalizationService,
            new DedupFingerprintService(normalizationService),
            db,
            Options.Create(new AutoTicketOptions
            {
                Enabled = true,
                ShadowMode = false,
                MaxCreatedTicketsPerHourPerAlertCode = 3
            }),
            NullLogger<AutoTicketOrchestratorService>.Instance);

        var result = await orchestrator.EvaluateAsync(monitoringEvent);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(AutoTicketDecision.RateLimited));
            Assert.That(result.CreatedTicketId, Is.Null);
            Assert.That(alertToTicketService.Requests, Has.Count.EqualTo(0));
            Assert.That(executionRepository.LookupCalls, Has.Count.EqualTo(1));
            Assert.That(executionRepository.CreatedCountLookupCalls, Has.Count.EqualTo(1));
            Assert.That(executionRepository.ReopenLookupCalls, Has.Count.EqualTo(0));
            Assert.That(dedupService.RegisteredTicketIds, Has.Count.EqualTo(0));
        });
    }

    [Test]
    public async Task CreateTicketFromAlertAsync_ShouldReturnExistingTicket_WhenAlertAlreadyHasLinkedTicket()
    {
        var existingTicket = new Ticket
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Title = "Existing Alert Ticket",
            Description = "Already linked",
            WorkflowStateId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var ticketRepository = new FakeTicketRepository(existingTicket);
        var alertRepository = new FakeAgentAlertRepository();
        var activityLogService = new FakeActivityLogService();
        var workflowRepository = new FakeWorkflowRepository();
        var service = new AlertToTicketService(
            ticketRepository,
            workflowRepository,
            alertRepository,
            activityLogService,
            NullLogger<AlertToTicketService>.Instance);

        var alert = new AgentAlertDefinition
        {
            Id = Guid.NewGuid(),
            Title = "Disk Full",
            Message = "Drive C is full.",
            TicketId = existingTicket.Id
        };

        var result = await service.CreateTicketFromAlertAsync(
            alert,
            existingTicket.ClientId,
            siteId: null,
            agentId: null,
            priority: TicketPriority.Critical);

        Assert.Multiple(() =>
        {
            Assert.That(result.Id, Is.EqualTo(existingTicket.Id));
            Assert.That(ticketRepository.CreatedTickets, Has.Count.EqualTo(0));
            Assert.That(alertRepository.UpdatedAlerts, Has.Count.EqualTo(0));
            Assert.That(activityLogService.Logs, Has.Count.EqualTo(0));
        });
    }

    [Test]
    public async Task CreateTicketFromMonitoringEventAsync_ShouldApplyRoutingOverrides()
    {
        var initialStateId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var workflowProfileId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var ticketRepository = new FakeTicketRepository();
        var workflowRepository = new FakeWorkflowRepository(initialStateId);
        var alertRepository = new FakeAgentAlertRepository();
        var activityLogService = new FakeActivityLogService();
        var service = new AlertToTicketService(
            ticketRepository,
            workflowRepository,
            alertRepository,
            activityLogService,
            NullLogger<AlertToTicketService>.Instance);

        var result = await service.CreateTicketFromMonitoringEventAsync(new AutoTicketCreateTicketRequest
        {
            ClientId = clientId,
            SiteId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            DepartmentId = departmentId,
            WorkflowProfileId = workflowProfileId,
            Title = "[Critical] disk.full",
            Description = "Disk usage reached 99%.",
            Category = "Capacity",
            Priority = TicketPriority.Critical,
            ActivityMessage = "Created automatically from monitoring event."
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.ClientId, Is.EqualTo(clientId));
            Assert.That(result.DepartmentId, Is.EqualTo(departmentId));
            Assert.That(result.WorkflowProfileId, Is.EqualTo(workflowProfileId));
            Assert.That(result.Category, Is.EqualTo("Capacity"));
            Assert.That(result.Priority, Is.EqualTo(TicketPriority.Critical));
            Assert.That(result.WorkflowStateId, Is.EqualTo(initialStateId));
            Assert.That(ticketRepository.CreatedTickets, Has.Count.EqualTo(1));
            Assert.That(activityLogService.Logs, Has.Count.EqualTo(1));
            Assert.That(alertRepository.UpdatedAlerts, Has.Count.EqualTo(0));
        });
    }

    [Test]
    public async Task TryAcquireOrGetAsync_ShouldReturnExistingTicket_WhenRepositorySignalsConcurrentInsert()
    {
        var existingTicketId = Guid.NewGuid();
        var repository = new RacingAlertCorrelationLockRepository(existingTicketId);
        var service = new AutoTicketDedupService(repository);

        var result = await service.TryAcquireOrGetAsync("client:agent:disk.full:hash:1", TimeSpan.FromMinutes(60));

        Assert.Multiple(() =>
        {
            Assert.That(result.Acquired, Is.False);
            Assert.That(result.ExistingTicketId, Is.EqualTo(existingTicketId));
            Assert.That(result.DedupKey, Is.EqualTo("client:agent:disk.full:hash:1"));
        });
    }

    private static DiscoveryDbContext CreateOrchestratorDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"auto-ticket-orchestrator-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AutoTicketTestDiscoveryDbContext(options);
    }

    private static AgentMonitoringEvent BuildMonitoringEvent()
    {
        var normalizationService = new MonitoringEventNormalizationService();

        return new AgentMonitoringEvent
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            AlertCode = "disk.full",
            Severity = MonitoringEventSeverity.Critical,
            Title = "Disk Full",
            Message = "Drive C reached 99% usage.",
            PayloadJson = "{\"disk\":\"C\",\"used\":99}",
            LabelsSnapshotJson = normalizationService.SerializeLabels(["servidor"]),
            Source = MonitoringEventSource.Automation,
            OccurredAt = DateTime.UtcNow
        };
    }

    private static AutoTicketRuleDecision BuildCreateDecision()
    {
        return new AutoTicketRuleDecision
        {
            Rule = new AutoTicketRule
            {
                Id = Guid.NewGuid(),
                Name = "disk.full servidor",
                Action = AutoTicketRuleAction.CreateTicket,
                ScopeLevel = AutoTicketScopeLevel.Client,
                PriorityOrder = 100,
                TargetDepartmentId = Guid.NewGuid(),
                TargetWorkflowProfileId = Guid.NewGuid(),
                TargetCategory = "Capacity",
                TargetPriority = TicketPriority.Critical,
                DedupWindowMinutes = 60,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            Decision = AutoTicketDecision.MatchedNoAction,
            Reason = "Rule matched and requested ticket creation."
        };
    }

    private sealed class FakeTicketRepository : ITicketRepository
    {
        private readonly Dictionary<Guid, Ticket> _tickets = new();

        public FakeTicketRepository(params Ticket[] initialTickets)
        {
            foreach (var ticket in initialTickets)
                _tickets[ticket.Id] = ticket;
        }

        public List<Ticket> CreatedTickets { get; } = [];

        public Task<Ticket?> GetByIdAsync(Guid id)
            => Task.FromResult(_tickets.TryGetValue(id, out var ticket) ? ticket : null);

        public Task<IEnumerable<Ticket>> GetByClientIdAsync(Guid clientId, Guid? workflowStateId = null)
            => Task.FromResult<IEnumerable<Ticket>>(_tickets.Values.Where(ticket => ticket.ClientId == clientId).ToList());

        public Task<IEnumerable<Ticket>> GetByAgentIdAsync(Guid agentId, Guid? workflowStateId = null)
            => Task.FromResult<IEnumerable<Ticket>>(_tickets.Values.Where(ticket => ticket.AgentId == agentId).ToList());

        public Task<IEnumerable<Ticket>> GetAllAsync(TicketFilterQuery filter)
            => Task.FromResult<IEnumerable<Ticket>>(_tickets.Values.ToList());

        public Task<Ticket> CreateAsync(Ticket ticket)
        {
            _tickets[ticket.Id] = ticket;
            CreatedTickets.Add(ticket);
            return Task.FromResult(ticket);
        }

        public Task UpdateAsync(Ticket ticket)
        {
            _tickets[ticket.Id] = ticket;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id)
        {
            _tickets.Remove(id);
            return Task.CompletedTask;
        }

        public Task UpdateWorkflowStateAsync(Guid id, Guid workflowStateId, DateTime? closedAt = null)
        {
            if (_tickets.TryGetValue(id, out var ticket))
            {
                ticket.WorkflowStateId = workflowStateId;
                ticket.ClosedAt = closedAt;
            }

            return Task.CompletedTask;
        }

        public Task<IEnumerable<TicketComment>> GetCommentsAsync(Guid ticketId)
            => Task.FromResult<IEnumerable<TicketComment>>([]);

        public Task<TicketComment> AddCommentAsync(TicketComment comment)
            => Task.FromResult(comment);

        public Task<List<Ticket>> GetOpenTicketsWithSlaAsync()
            => Task.FromResult(_tickets.Values.Where(ticket => ticket.ClosedAt is null).ToList());

        public Task UpdateSlaHoldAsync(Guid id, DateTime? slaHoldStartedAt, int slaPausedSeconds)
            => Task.CompletedTask;

        public Task UpdateFirstRespondedAtAsync(Guid id, DateTime firstRespondedAt)
        {
            if (_tickets.TryGetValue(id, out var ticket))
                ticket.FirstRespondedAt = firstRespondedAt;

            return Task.CompletedTask;
        }

        public Task<TicketKpiResult> GetKpiAsync(Guid? clientId, Guid? departmentId, DateTime? since)
            => Task.FromResult(new TicketKpiResult(
                TotalOpen: _tickets.Values.Count(ticket => ticket.ClosedAt is null),
                TotalClosed: _tickets.Values.Count(ticket => ticket.ClosedAt is not null),
                SlaBreached: 0,
                SlaWarning: 0,
                OnHold: 0,
                FrtAchievementRate: 1,
                AvgResolutionHours: 0,
                AvgAgeOpenHours: 0,
                ByAssignee: [],
                ByDepartment: []));
    }

    private sealed class FakeWorkflowRepository : IWorkflowRepository
    {
        private readonly WorkflowState _initialState;

        public Guid InitialStateId => _initialState.Id;

        public FakeWorkflowRepository(Guid? stateId = null)
        {
            _initialState = new WorkflowState
            {
                Id = stateId ?? Guid.NewGuid(),
                Name = "Open",
                IsInitial = true,
                CreatedAt = DateTime.UtcNow
            };
        }

        public Task<WorkflowState?> GetStateByIdAsync(Guid id)
            => Task.FromResult(id == _initialState.Id ? _initialState : null);

        public Task<IEnumerable<WorkflowState>> GetStatesAsync(Guid? clientId = null)
            => Task.FromResult<IEnumerable<WorkflowState>>([_initialState]);

        public Task<WorkflowState?> GetInitialStateAsync(Guid? clientId = null)
            => Task.FromResult<WorkflowState?>(_initialState);

        public Task<WorkflowState> CreateStateAsync(WorkflowState state)
            => Task.FromResult(state);

        public Task UpdateStateAsync(WorkflowState state)
            => Task.CompletedTask;

        public Task DeleteStateAsync(Guid id)
            => Task.CompletedTask;

        public Task<IEnumerable<WorkflowTransition>> GetTransitionsAsync(Guid? clientId = null)
            => Task.FromResult<IEnumerable<WorkflowTransition>>([]);

        public Task<IEnumerable<WorkflowTransition>> GetTransitionsFromStateAsync(Guid fromStateId, Guid? clientId = null)
            => Task.FromResult<IEnumerable<WorkflowTransition>>([]);

        public Task<bool> IsTransitionValidAsync(Guid fromStateId, Guid toStateId, Guid? clientId = null)
            => Task.FromResult(true);

        public Task<WorkflowTransition> CreateTransitionAsync(WorkflowTransition transition)
            => Task.FromResult(transition);

        public Task DeleteTransitionAsync(Guid id)
            => Task.CompletedTask;
    }

    private sealed class FakeAutoTicketRuleEngineService : IAutoTicketRuleEngineService
    {
        private readonly AutoTicketRuleDecision _decision;

        public FakeAutoTicketRuleEngineService(AutoTicketRuleDecision decision)
        {
            _decision = decision;
        }

        public Task<AutoTicketRuleDecision> EvaluateAsync(
            AgentMonitoringEvent monitoringEvent,
            IReadOnlyCollection<string> labels,
            IReadOnlyCollection<AutoTicketRule>? candidateRules = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_decision);
    }

    private sealed class FakeAutoTicketRuleExecutionRepository : IAutoTicketRuleExecutionRepository
    {
        public List<AutoTicketRuleExecution> CreatedExecutions { get; } = [];
        public List<(Guid ClientId, Guid AgentId, string AlertCode, Guid? DepartmentId, Guid? WorkflowProfileId, string? Category)> LookupCalls { get; } = [];
        public List<(Guid ClientId, string AlertCode, DateTime SinceUtc)> CreatedCountLookupCalls { get; } = [];
        public List<(Guid ClientId, Guid AgentId, string AlertCode, DateTime ClosedAfterUtc, Guid? DepartmentId, Guid? WorkflowProfileId, string? Category)> ReopenLookupCalls { get; } = [];
        public Guid? ReusableOpenTicketId { get; set; }
        public Guid? ReopenableClosedTicketId { get; set; }
        public int CreatedCountForClientAlert { get; set; }
        public AutoTicketRuleStatsSnapshot StatsSnapshot { get; set; } = new();

        public Task<AutoTicketRuleExecution> CreateAsync(AutoTicketRuleExecution execution)
        {
            CreatedExecutions.Add(execution);
            return Task.FromResult(execution);
        }

        public Task<IReadOnlyList<AutoTicketRuleExecution>> GetByMonitoringEventIdAsync(Guid monitoringEventId)
            => Task.FromResult<IReadOnlyList<AutoTicketRuleExecution>>(CreatedExecutions
                .Where(execution => execution.MonitoringEventId == monitoringEventId)
                .ToList());

        public Task<int> GetCreatedCountForClientAlertAsync(Guid clientId, string alertCode, DateTime sinceUtc)
        {
            CreatedCountLookupCalls.Add((clientId, alertCode, sinceUtc));
            return Task.FromResult(CreatedCountForClientAlert);
        }

        public Task<Guid?> GetReusableOpenTicketIdAsync(
            Guid clientId,
            Guid agentId,
            string alertCode,
            Guid? departmentId = null,
            Guid? workflowProfileId = null,
            string? category = null)
        {
            LookupCalls.Add((clientId, agentId, alertCode, departmentId, workflowProfileId, category));
            return Task.FromResult(ReusableOpenTicketId);
        }

        public Task<Guid?> GetReopenableClosedTicketIdAsync(
            Guid clientId,
            Guid agentId,
            string alertCode,
            DateTime closedAfterUtc,
            Guid? departmentId = null,
            Guid? workflowProfileId = null,
            string? category = null)
        {
            ReopenLookupCalls.Add((clientId, agentId, alertCode, closedAfterUtc, departmentId, workflowProfileId, category));
            return Task.FromResult(ReopenableClosedTicketId);
        }

        public Task<AutoTicketRuleStatsSnapshot> GetRuleStatsAsync(AutoTicketRule rule, DateTime periodStartUtc, DateTime periodEndUtc)
            => Task.FromResult(StatsSnapshot);
    }

    private sealed class FakeAutoTicketDedupService : IAutoTicketDedupService
    {
        public List<string> DedupKeys { get; } = [];
        public List<Guid> RegisteredTicketIds { get; } = [];

        public Task<AutoTicketDedupResult> TryAcquireOrGetAsync(string dedupKey, TimeSpan dedupWindow, CancellationToken cancellationToken = default)
        {
            DedupKeys.Add(dedupKey);
            return Task.FromResult(new AutoTicketDedupResult
            {
                DedupKey = dedupKey,
                Acquired = true,
                ExpiresAt = DateTime.UtcNow.Add(dedupWindow)
            });
        }

        public Task RegisterCreatedTicketAsync(string dedupKey, Guid ticketId, CancellationToken cancellationToken = default)
        {
            DedupKeys.Add(dedupKey);
            RegisteredTicketIds.Add(ticketId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAlertToTicketService : IAlertToTicketService
    {
        public List<AutoTicketCreateTicketRequest> Requests { get; } = [];
        public Ticket? TicketToReturn { get; set; }

        public Task<Ticket> CreateTicketFromAlertAsync(AgentAlertDefinition alert, Guid clientId, Guid? siteId, Guid? agentId, TicketPriority priority = TicketPriority.Medium, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Ticket> CreateTicketFromMonitoringEventAsync(AutoTicketCreateTicketRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(TicketToReturn ?? new Ticket
            {
                Id = Guid.NewGuid(),
                ClientId = request.ClientId,
                SiteId = request.SiteId,
                AgentId = request.AgentId,
                DepartmentId = request.DepartmentId,
                WorkflowProfileId = request.WorkflowProfileId,
                Title = request.Title,
                Description = request.Description,
                Category = request.Category,
                Priority = request.Priority,
                WorkflowStateId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
    }

    private sealed class FakeAgentAlertRepository : IAgentAlertRepository
    {
        public List<AgentAlertDefinition> UpdatedAlerts { get; } = [];

        public Task<AgentAlertDefinition?> GetByIdAsync(Guid id)
            => Task.FromResult<AgentAlertDefinition?>(null);

        public Task<IReadOnlyList<AgentAlertDefinition>> GetByFiltersAsync(
            AlertDefinitionStatus? status = null,
            AlertScopeType? scopeType = null,
            Guid? scopeClientId = null,
            Guid? scopeSiteId = null,
            Guid? scopeAgentId = null,
            Guid? ticketId = null,
            int limit = 100,
            int offset = 0)
            => Task.FromResult<IReadOnlyList<AgentAlertDefinition>>([]);

        public Task<IReadOnlyList<AgentAlertDefinition>> GetPendingScheduledAsync(DateTime utcNow)
            => Task.FromResult<IReadOnlyList<AgentAlertDefinition>>([]);

        public Task<IReadOnlyList<AgentAlertDefinition>> GetExpiredAsync(DateTime utcNow)
            => Task.FromResult<IReadOnlyList<AgentAlertDefinition>>([]);

        public Task<AgentAlertDefinition> CreateAsync(AgentAlertDefinition alert)
            => Task.FromResult(alert);

        public Task UpdateAsync(AgentAlertDefinition alert)
        {
            UpdatedAlerts.Add(alert);
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(Guid id, AlertDefinitionStatus status, DateTime? dispatchedAt = null, int? dispatchedCount = null)
            => Task.CompletedTask;
    }

    private sealed class FakeActivityLogService : IActivityLogService
    {
        public List<TicketActivityLog> Logs { get; } = [];

        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
            => Add(ticketId, TicketActivityType.StateChanged);

        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
            => Add(ticketId, TicketActivityType.Assigned);

        public Task<TicketActivityLog> LogActivityAsync(Guid ticketId, TicketActivityType type, Guid? changedByUserId, string? oldValue = null, string? newValue = null, string? comment = null)
            => Add(ticketId, type, oldValue, newValue, comment);

        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
            => Add(ticketId, TicketActivityType.PriorityChanged, oldPriority, newPriority);

        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
            => Add(ticketId, TicketActivityType.DepartmentChanged, oldDept, newDept);

        private Task<TicketActivityLog> Add(Guid ticketId, TicketActivityType type, string? oldValue = null, string? newValue = null, string? comment = null)
        {
            var log = new TicketActivityLog
            {
                Id = Guid.NewGuid(),
                TicketId = ticketId,
                Type = type,
                OldValue = oldValue,
                NewValue = newValue,
                Comment = comment,
                CreatedAt = DateTime.UtcNow
            };

            Logs.Add(log);
            return Task.FromResult(log);
        }
    }

    private sealed class RacingAlertCorrelationLockRepository : IAlertCorrelationLockRepository
    {
        private readonly Guid _existingTicketId;
        private AlertCorrelationLock? _storedLock;

        public RacingAlertCorrelationLockRepository(Guid existingTicketId)
        {
            _existingTicketId = existingTicketId;
        }

        public Task<AlertCorrelationLock?> GetByDedupKeyAsync(string dedupKey)
            => Task.FromResult(_storedLock is not null && _storedLock.DedupKey == dedupKey ? _storedLock : null);

        public Task<AlertCorrelationLock> CreateAsync(AlertCorrelationLock correlationLock)
        {
            _storedLock = new AlertCorrelationLock
            {
                DedupKey = correlationLock.DedupKey,
                ExpiresAt = correlationLock.ExpiresAt,
                LastAlertAt = correlationLock.LastAlertAt,
                LastTicketId = _existingTicketId
            };

            throw new DbUpdateException("Unique key violation.");
        }

        public Task<AlertCorrelationLock> UpdateAsync(AlertCorrelationLock correlationLock)
        {
            _storedLock = correlationLock;
            return Task.FromResult(correlationLock);
        }
    }

    private sealed class AutoTicketTestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Ticket),
                typeof(AgentMonitoringEvent),
                typeof(AutoTicketRuleExecution)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Ignore(item => item.DaysOpen);
            });

            modelBuilder.Entity<AgentMonitoringEvent>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.AlertCode).IsRequired();
            });

            modelBuilder.Entity<AutoTicketRuleExecution>(entity =>
            {
                entity.HasKey(item => item.Id);
            });
        }
    }
}