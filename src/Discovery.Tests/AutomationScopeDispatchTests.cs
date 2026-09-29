using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Operações em massa por cliente/site.
///
/// Regras travadas aqui:
///  - exatamente um escopo (clientId OU siteId);
///  - site precisa pertencer ao cliente informado (evita escopo cruzado);
///  - agentes excluídos/offline/em manutenção não recebem comando (NATS core não
///    retém mensagem para agente desconectado — enfileirar seria mentira);
///  - um agente que falha não aborta o lote;
///  - cada agente despachado gera seu próprio report, com o correlation do lote.
/// </summary>
[TestFixture]
public class AutomationScopeDispatchTests
{
    private static readonly Guid SiteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ClientId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    // ── Validação pura ────────────────────────────────────────────────────

    [Test]
    public void ValidateTarget_RequiresExactlyOneScope()
    {
        Assert.That(AutomationScopeValidation.ValidateTarget(null, null), Is.Not.Null);
        Assert.That(AutomationScopeValidation.ValidateTarget(ClientId, SiteId), Is.Not.Null);
        Assert.That(AutomationScopeValidation.ValidateTarget(ClientId, null), Is.Null);
        Assert.That(AutomationScopeValidation.ValidateTarget(null, SiteId), Is.Null);
    }

    [Test]
    public void ValidateSiteOwnership_RejectsSiteFromAnotherClient()
    {
        var site = new Site { Id = SiteId, ClientId = ClientId };

        Assert.That(AutomationScopeValidation.ValidateSiteOwnership(null, null), Is.Not.Null, "site inexistente deve falhar");
        Assert.That(AutomationScopeValidation.ValidateSiteOwnership(site, ClientId), Is.Null);
        Assert.That(AutomationScopeValidation.ValidateSiteOwnership(site, Guid.NewGuid()), Is.Not.Null,
            "site de outro cliente não pode ser aceito só porque o cliente foi liberado");
    }

    [Test]
    public void ValidateBatchSize_RejectsAboveLimit()
    {
        Assert.That(AutomationScopeValidation.ValidateBatchSize(AutomationScopeValidation.MaxAgentsPerBatch), Is.Null);
        Assert.That(AutomationScopeValidation.ValidateBatchSize(AutomationScopeValidation.MaxAgentsPerBatch + 1), Is.Not.Null);
    }

    [Test]
    public void ResolveCorrelationId_UsesProvidedValueOrGeneratesBatchId()
    {
        Assert.That(AutomationScopeBatch.ResolveCorrelationId("  lote-1  ", "site"), Is.EqualTo("lote-1"));

        var generated = AutomationScopeBatch.ResolveCorrelationId(null, "client");
        Assert.That(generated, Does.StartWith("bulk-client-"));
    }

    // ── Loop de disparo ───────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_QueuesOfflineAgentsAndSkipsDeletedAndMaintenance()
    {
        var online = NewAgent("PC-ONLINE", AgentStatus.Online);
        var offline = NewAgent("PC-OFFLINE", AgentStatus.Offline);
        var maintenance = NewAgent("PC-MANUTENCAO", AgentStatus.Maintenance);
        var deleted = NewAgent("PC-LIXEIRA", AgentStatus.Online, deleted: true);

        var dispatcher = new FakeDispatcher();
        var reports = new CapturingReportRepo();

        var result = await DispatchAsync([online, offline, maintenance, deleted], dispatcher, reports);

        Assert.That(result.TotalAgents, Is.EqualTo(3), "agente excluído não entra no escopo");
        Assert.That(result.Dispatched, Is.EqualTo(1));
        Assert.That(result.Queued, Is.EqualTo(1), "offline fica na fila para a reconexão");
        Assert.That(result.SkippedMaintenance, Is.EqualTo(1));
        Assert.That(result.SkippedOffline, Is.Zero);
        Assert.That(result.EligibleAgents, Is.EqualTo(2));

        Assert.That(dispatcher.Dispatched.Select(c => c.AgentId),
            Is.EquivalentTo(new[] { online.Id, offline.Id }),
            "o comando do agente offline precisa ser persistido para a reentrega");
        Assert.That(reports.Created, Has.Count.EqualTo(2), "um report por agente elegível");

        Assert.That(result.Items.Single(i => i.AgentId == offline.Id).Status,
            Is.EqualTo(AutomationScopeDispatchStatus.Queued));
    }

    [Test]
    public async Task DispatchAsync_SkipsOfflineAgentsWhenRedeliveryIsDisabled()
    {
        var online = NewAgent("PC-ONLINE", AgentStatus.Online);
        var offline = NewAgent("PC-OFFLINE", AgentStatus.Offline);

        var dispatcher = new FakeDispatcher();
        var reports = new CapturingReportRepo();

        var result = await DispatchAsync([online, offline], dispatcher, reports, queueOfflineAgents: false);

        Assert.That(result.Dispatched, Is.EqualTo(1));
        Assert.That(result.Queued, Is.Zero);
        Assert.That(result.SkippedOffline, Is.EqualTo(1));
        Assert.That(dispatcher.Dispatched, Has.Count.EqualTo(1), "sem reentrega não criamos comando órfão");
        Assert.That(result.Items.Single(i => i.AgentId == offline.Id).Status,
            Is.EqualTo(AutomationScopeDispatchStatus.SkippedOffline));
    }

    [Test]
    public async Task DispatchAsync_CarriesBatchCorrelationIntoEveryReport()
    {
        var agents = new[] { NewAgent("PC-1", AgentStatus.Online), NewAgent("PC-2", AgentStatus.Online) };
        var dispatcher = new FakeDispatcher();
        var reports = new CapturingReportRepo();

        var result = await DispatchAsync(agents, dispatcher, reports);

        Assert.That(result.Dispatched, Is.EqualTo(2));
        Assert.That(reports.Created, Has.Count.EqualTo(2));
        Assert.That(reports.Created.Select(r => r.CorrelationId).Distinct().Single(), Is.EqualTo("lote-teste"));
        Assert.That(reports.Created.All(r => r.TaskId == TaskId), Is.True);
        Assert.That(reports.Created.All(r => r.Status == AutomationExecutionStatus.Dispatched), Is.True);
    }

    [Test]
    public async Task DispatchAsync_AgentFailureDoesNotAbortTheBatch()
    {
        var failing = NewAgent("PC-FALHA", AgentStatus.Online);
        var healthy = NewAgent("PC-OK", AgentStatus.Online);

        var dispatcher = new FakeDispatcher { FailFor = failing.Id };
        var reports = new CapturingReportRepo();

        var result = await DispatchAsync([failing, healthy], dispatcher, reports);

        Assert.That(result.Dispatched, Is.EqualTo(1));
        Assert.That(result.Failed, Is.EqualTo(1));

        var failedItem = result.Items.Single(i => i.AgentId == failing.Id);
        Assert.That(failedItem.Status, Is.EqualTo(AutomationScopeDispatchStatus.Failed));
        Assert.That(failedItem.Error, Is.Not.Null.And.Not.Empty);

        Assert.That(result.Items.Single(i => i.AgentId == healthy.Id).Status,
            Is.EqualTo(AutomationScopeDispatchStatus.Dispatched));
        Assert.That(reports.Created, Has.Count.EqualTo(1), "agente que falhou não gera report");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static readonly Guid TaskId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static Agent NewAgent(string hostname, AgentStatus status, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = SiteId,
        Hostname = hostname,
        Status = status,
        MaintenanceEnabled = status == AgentStatus.Maintenance,
        DeletedAt = deleted ? DateTime.UtcNow : null
    };

    private static Task<AutomationScopeDispatchResultDto> DispatchAsync(
        IReadOnlyList<Agent> agents,
        FakeDispatcher dispatcher,
        CapturingReportRepo reports,
        bool queueOfflineAgents = true)
        => AutomationScopeDispatcher.DispatchAsync(
            agents,
            "site",
            SiteId,
            CommandType.Script,
            "echo 1",
            "lote-teste",
            TaskId,
            null,
            AutomationExecutionSourceType.RunNow,
            new { mode = "task-run-now", batch = true },
            queueOfflineAgents,
            dispatcher,
            reports,
            CancellationToken.None);

    private sealed class FakeDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Dispatched { get; } = [];
        public Guid? FailFor { get; init; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            if (FailFor.HasValue && command.AgentId == FailFor.Value)
                throw new InvalidOperationException("falha simulada de dispatch");

            command.Id = Guid.NewGuid();
            Dispatched.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class CapturingReportRepo : IAutomationExecutionReportRepository
    {
        public List<AutomationExecutionReport> Created { get; } = [];

        public Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report)
        {
            report.Id = Guid.NewGuid();
            Created.Add(report);
            return Task.FromResult(report);
        }

        public Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId) => throw new NotSupportedException();
        public Task<AutomationExecutionReport?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<bool> MarkCancelledAsync(Guid executionId, string? errorMessage, DateTime cancelledAt) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(
            Guid agentId,
            int limit = 100,
            AutomationExecutionStatus? status = null,
            AutomationExecutionSourceType? sourceType = null,
            Guid? taskId = null,
            Guid? scriptId = null,
            string? correlationId = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100) => throw new NotSupportedException();
        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt) => throw new NotSupportedException();
        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId) => throw new NotSupportedException();
    }
}
