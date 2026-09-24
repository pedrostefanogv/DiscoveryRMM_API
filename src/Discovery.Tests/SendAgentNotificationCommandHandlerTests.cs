using System.Text.Json;
using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Tests;

[TestFixture]
public class SendAgentNotificationCommandHandlerTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task Modal_WithoutTimeout_ShouldWaitForUserAndOmitTimeout()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(AgentId), dispatcher);

        var result = await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "Aviso", "Mensagem", PsadtAlertType.Modal, null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var payload = ParsePayload(dispatcher.Last!);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("type").GetString(), Is.EqualTo("modal"));
            Assert.That(payload.GetProperty("waitForUser").GetBoolean(), Is.True);
            Assert.That(payload.TryGetProperty("timeoutSeconds", out _), Is.False);
        });
    }

    [Test]
    public async Task Modal_WithTimeout_ShouldSetTimeoutAndNotWaitForUser()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(AgentId), dispatcher);

        await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "Aviso", "Mensagem", PsadtAlertType.Modal, 600),
            CancellationToken.None);

        var payload = ParsePayload(dispatcher.Last!);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("waitForUser").GetBoolean(), Is.False);
            Assert.That(payload.GetProperty("timeoutSeconds").GetInt32(), Is.EqualTo(600));
        });
    }

    [Test]
    public async Task Modal_TimeoutAbovePsadtLimit_ShouldClamp()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(AgentId), dispatcher);

        await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "Aviso", "Mensagem", PsadtAlertType.Modal, 99999),
            CancellationToken.None);

        var payload = ParsePayload(dispatcher.Last!);
        Assert.That(payload.GetProperty("timeoutSeconds").GetInt32(), Is.EqualTo(3300));
    }

    [Test]
    public async Task Toast_WithoutTimeout_ShouldDefaultTo15Seconds()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(AgentId), dispatcher);

        await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "Aviso", "Mensagem", PsadtAlertType.Toast, null),
            CancellationToken.None);

        var payload = ParsePayload(dispatcher.Last!);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("type").GetString(), Is.EqualTo("toast"));
            Assert.That(payload.GetProperty("waitForUser").GetBoolean(), Is.False);
            Assert.That(payload.GetProperty("timeoutSeconds").GetInt32(), Is.EqualTo(15));
        });
    }

    [Test]
    public async Task EmptyTitle_ShouldFailWithoutDispatching()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(AgentId), dispatcher);

        var result = await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "  ", "Mensagem", PsadtAlertType.Modal, null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(dispatcher.Last, Is.Null);
    }

    [Test]
    public async Task UnknownAgent_ShouldReturnNotFound()
    {
        var dispatcher = new CapturingDispatcher();
        var handler = new SendAgentNotificationCommandHandler(new FakeAgentRepository(null), dispatcher);

        var result = await handler.Handle(
            new SendAgentNotificationCommand(AgentId, "Aviso", "Mensagem", PsadtAlertType.Modal, null),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(dispatcher.Last, Is.Null);
    }

    private static JsonElement ParsePayload(AgentCommand command)
    {
        using var document = JsonDocument.Parse(command.Payload);
        return document.RootElement.Clone();
    }

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public AgentCommand? Last { get; private set; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Last = command;
            return Task.FromResult(command);
        }
    }

    private sealed class FakeAgentRepository(Guid? existingAgentId) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult(existingAgentId == id ? new Agent { Id = id, Hostname = "host-1" } : null);

        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);

        public Task<IEnumerable<Agent>> GetAllAsync()
            => Task.FromResult<IEnumerable<Agent>>([]);

        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
            => Task.FromResult<IEnumerable<Agent>>([]);

        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
            => Task.FromResult<IEnumerable<Agent>>([]);

        public Task<Agent> CreateAsync(Agent agent)
            => Task.FromResult(agent);

        public Task UpdateAsync(Agent agent)
            => Task.CompletedTask;

        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);

        public Task ApproveZeroTouchAsync(Guid agentId)
            => Task.CompletedTask;

        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId)
            => Task.CompletedTask;

        public Task TransferSiteAsync(Guid agentId, Guid newSiteId)
            => Task.CompletedTask;

        public Task DeleteAsync(Guid id)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>([]);
    }
}
