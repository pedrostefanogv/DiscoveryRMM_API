using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Tests;

[TestFixture]
public class SendScopeNotificationCommandHandlerTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SiteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AgentA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SendScopeNotificationCommandHandler BuildHandler(
        FakeAgentRepository repo,
        CapturingDispatcher dispatcher,
        FakeLabelRepository? labels = null)
        => new(repo, labels ?? new FakeLabelRepository(), dispatcher);

    [Test]
    public async Task ClientScope_ShouldDispatchToEveryAgentOfClient()
    {
        var dispatcher = new CapturingDispatcher();
        var repo = new FakeAgentRepository { ClientAgents = [AgentA, AgentB] };

        var result = await BuildHandler(repo, dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "Aviso", "Mensagem", ScopeClientId: ClientId),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Value!.TotalAgents, Is.EqualTo(2));
            Assert.That(result.Value!.Dispatched, Is.EqualTo(2));
            Assert.That(result.Value!.Failed, Is.EqualTo(0));
            Assert.That(dispatcher.Commands, Has.Count.EqualTo(2));
            Assert.That(dispatcher.Commands.Select(c => c.AgentId), Is.EquivalentTo(new[] { AgentA, AgentB }));
            Assert.That(dispatcher.Commands.All(c => c.CommandType == CommandType.ShowPsadtAlert), Is.True);
        });
    }

    [Test]
    public async Task SiteScope_ShouldDispatchToEveryAgentOfSite()
    {
        var dispatcher = new CapturingDispatcher();
        var repo = new FakeAgentRepository { SiteAgents = [AgentA] };

        var result = await BuildHandler(repo, dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Site, "Aviso", "Mensagem", ScopeSiteId: SiteId),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Dispatched, Is.EqualTo(1));
    }

    [Test]
    public async Task EmptyScope_ShouldSucceedWithZeroDispatches()
    {
        var dispatcher = new CapturingDispatcher();
        var result = await BuildHandler(new FakeAgentRepository(), dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "Aviso", "Mensagem", ScopeClientId: ClientId),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.TotalAgents, Is.EqualTo(0));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task ClientScope_WithoutClientId_ShouldFailWithValidation()
    {
        var dispatcher = new CapturingDispatcher();
        var result = await BuildHandler(new FakeAgentRepository(), dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "Aviso", "Mensagem"),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Validation"));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task EmptyTitle_ShouldFailWithoutDispatching()
    {
        var dispatcher = new CapturingDispatcher();
        var repo = new FakeAgentRepository { ClientAgents = [AgentA] };

        var result = await BuildHandler(repo, dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "  ", "Mensagem", ScopeClientId: ClientId),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task PartialFailure_ShouldContinueAndCountFailures()
    {
        var dispatcher = new CapturingDispatcher { FailForAgent = AgentA };
        var repo = new FakeAgentRepository { ClientAgents = [AgentA, AgentB] };

        var result = await BuildHandler(repo, dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "Aviso", "Mensagem", ScopeClientId: ClientId),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Value!.TotalAgents, Is.EqualTo(2));
            Assert.That(result.Value!.Dispatched, Is.EqualTo(1));
            Assert.That(result.Value!.Failed, Is.EqualTo(1));
            Assert.That(dispatcher.Commands, Has.Count.EqualTo(1));
            Assert.That(dispatcher.Commands[0].AgentId, Is.EqualTo(AgentB));
        });
    }

    [Test]
    public async Task LabelScope_ShouldResolveAgentsByLabel()
    {
        var dispatcher = new CapturingDispatcher();
        var repo = new FakeAgentRepository();
        var labels = new FakeLabelRepository { AgentIdsByLabel = { ["servidores"] = [AgentA, AgentB] } };

        var result = await BuildHandler(repo, dispatcher, labels).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Label, "Aviso", "Mensagem", ScopeLabelName: "servidores"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Dispatched, Is.EqualTo(2));
    }

    [Test]
    public async Task Broadcast_ShouldShareTheSameAlertIdAcrossAgents()
    {
        var dispatcher = new CapturingDispatcher();
        var repo = new FakeAgentRepository { ClientAgents = [AgentA, AgentB] };

        await BuildHandler(repo, dispatcher).Handle(
            new SendScopeNotificationCommand(AlertScopeType.Client, "Aviso", "Mensagem", ScopeClientId: ClientId),
            CancellationToken.None);

        var ids = dispatcher.Commands
            .Select(c => System.Text.Json.JsonDocument.Parse(c.Payload).RootElement.GetProperty("alertId").GetString())
            .Distinct()
            .ToList();

        Assert.That(ids, Has.Count.EqualTo(1));
    }

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Commands { get; } = [];
        public Guid? FailForAgent { get; init; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            if (FailForAgent.HasValue && command.AgentId == FailForAgent.Value)
                throw new InvalidOperationException("agent offline");

            Commands.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class FakeAgentRepository : IAgentRepository
    {
        public IReadOnlyList<Guid> ClientAgents { get; init; } = [];
        public IReadOnlyList<Guid> SiteAgents { get; init; } = [];

        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult<Agent?>(new Agent { Id = id, Hostname = "host" });

        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);

        public Task<IEnumerable<Agent>> GetAllAsync()
            => Task.FromResult<IEnumerable<Agent>>([]);

        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
            => Task.FromResult(SiteAgents.Select(id => new Agent { Id = id, Hostname = "host" }));

        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
            => Task.FromResult(ClientAgents.Select(id => new Agent { Id = id, Hostname = "host" }));

        public Task<Agent> CreateAsync(Agent agent) => Task.FromResult(agent);
        public Task UpdateAsync(Agent agent) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class FakeLabelRepository : IAgentLabelRepository
    {
        public Dictionary<string, Guid[]> AgentIdsByLabel { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<Guid>> GetAgentIdsByLabelPagedAsync(string label, Guid? afterAgentId, int limit, CancellationToken ct = default)
        {
            var ids = AgentIdsByLabel.TryGetValue(label, out var value) ? value : [];
            IReadOnlyList<Guid> page = ids
                .OrderBy(id => id)
                .Where(id => afterAgentId is null || id.CompareTo(afterAgentId.Value) > 0)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }

        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<IReadOnlyList<AgentLabel>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds) => Task.FromResult<IReadOnlyList<AgentLabel>>([]);
        public Task<(int Total, IReadOnlyList<AgentLabelRuleAgentResponse> Agents)> GetAgentsByRuleIdPagedAsync(Guid ruleId, int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult((0, (IReadOnlyList<AgentLabelRuleAgentResponse>)[]));
        public Task<int> CountAgentsByLabelAsync(string label, CancellationToken ct = default)
            => Task.FromResult(AgentIdsByLabel.TryGetValue(label, out var v) ? v.Length : 0);
        public Task<IReadOnlyList<AgentLabelUsageDto>> GetLabelUsageAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelUsageDto>>([]);
        public Task<IReadOnlyList<string>> GetDistinctLabelsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentLabel?> GetByIdAsync(Guid id) => Task.FromResult<AgentLabel?>(null);
        public Task SuppressAutomaticLabelAsync(Guid agentId, string label, string? suppressedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearSuppressionAsync(Guid agentId, string label, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentLabelSuppressionDto>> GetSuppressionsByAgentIdAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AgentLabelSuppressionDto>>([]);
        public Task<bool> ReleaseSuppressionAsync(Guid suppressionId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<AgentLabel> AddAsync(AgentLabel label) => Task.FromResult(label);
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }
}
