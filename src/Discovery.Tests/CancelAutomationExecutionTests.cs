using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Cancelamento de execução pendente.
///
/// Dois efeitos precisam acontecer juntos: o COMANDO vira terminal (senão a
/// reentrega continuaria reenviando para agentes offline) e a EXECUÇÃO deixa de
/// aparecer como "em andamento" no histórico. Também vale como teste de
/// anti-IDOR: não se cancela execução de outro agente.
/// </summary>
[TestFixture]
public class CancelAutomationExecutionTests
{
    private static readonly Guid AgentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgent = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Test]
    public async Task CancelPendingExecution_MarksCommandAndReportAsCancelled()
    {
        var commandId = Guid.NewGuid();
        var reports = new FakeReportRepo(NewReport(AgentId, commandId, AutomationExecutionStatus.Dispatched));
        var commands = new FakeCommandRepo(NewCommand(commandId, AgentId, CommandStatus.Sent));

        var result = await Handler(reports, commands).Handle(
            new CancelAutomationExecutionCommand(AgentId, reports.Single.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(commands.Single.Status, Is.EqualTo(CommandStatus.Cancelled),
            "comando terminal é o que remove o item da reentrega");
        Assert.That(reports.Single.Status, Is.EqualTo(AutomationExecutionStatus.Cancelled));
        Assert.That(reports.Single.ErrorMessage, Is.EqualTo("Cancelada pelo operador."));
        Assert.That(reports.Single.ResultReceivedAt, Is.Not.Null);
    }

    [TestCase(AutomationExecutionStatus.Completed)]
    [TestCase(AutomationExecutionStatus.Failed)]
    [TestCase(AutomationExecutionStatus.Cancelled)]
    public async Task CancelTerminalExecution_ReturnsConflict(AutomationExecutionStatus status)
    {
        var commandId = Guid.NewGuid();
        var reports = new FakeReportRepo(NewReport(AgentId, commandId, status));
        var commands = new FakeCommandRepo(NewCommand(commandId, AgentId, CommandStatus.Completed));

        var result = await Handler(reports, commands).Handle(
            new CancelAutomationExecutionCommand(AgentId, reports.Single.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("Conflict"));
        Assert.That(commands.Single.Status, Is.EqualTo(CommandStatus.Completed));
    }

    [Test]
    public async Task CancelExecutionOfAnotherAgent_ReturnsNotFound()
    {
        var commandId = Guid.NewGuid();
        var reports = new FakeReportRepo(NewReport(AgentId, commandId, AutomationExecutionStatus.Dispatched));
        var commands = new FakeCommandRepo(NewCommand(commandId, AgentId, CommandStatus.Sent));

        var result = await Handler(reports, commands).Handle(
            new CancelAutomationExecutionCommand(OtherAgent, reports.Single.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(commands.Single.Status, Is.EqualTo(CommandStatus.Sent));
        Assert.That(reports.Single.Status, Is.EqualTo(AutomationExecutionStatus.Dispatched));
    }

    [Test]
    public async Task CancelExecutionWithoutCommand_StillCancelsTheReport()
    {
        // Execuções automáticas (policy-sync) não têm comando despachado pelo servidor.
        var reports = new FakeReportRepo(NewReport(AgentId, commandId: null, AutomationExecutionStatus.Acknowledged));
        var commands = new FakeCommandRepo();

        var result = await Handler(reports, commands).Handle(
            new CancelAutomationExecutionCommand(AgentId, reports.Single.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(reports.Single.Status, Is.EqualTo(AutomationExecutionStatus.Cancelled));
        Assert.That(commands.Updated, Is.Empty);
    }

    [Test]
    public async Task CancelWhenCommandAlreadyExpired_LeavesCommandUntouched()
    {
        var commandId = Guid.NewGuid();
        var reports = new FakeReportRepo(NewReport(AgentId, commandId, AutomationExecutionStatus.Dispatched));
        var commands = new FakeCommandRepo(NewCommand(commandId, AgentId, CommandStatus.Timeout));

        var result = await Handler(reports, commands).Handle(
            new CancelAutomationExecutionCommand(AgentId, reports.Single.Id), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(commands.Updated, Is.Empty, "estado terminal do comando não deve ser sobrescrito");
        Assert.That(reports.Single.Status, Is.EqualTo(AutomationExecutionStatus.Cancelled));
    }

    private static CancelAutomationExecutionCommandHandler Handler(
        FakeReportRepo reports, FakeCommandRepo commands) => new(reports, commands);

    private static AutomationExecutionReport NewReport(
        Guid agentId, Guid? commandId, AutomationExecutionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        CommandId = commandId,
        AgentId = agentId,
        SourceType = AutomationExecutionSourceType.RunNow,
        Status = status,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentCommand NewCommand(Guid id, Guid agentId, CommandStatus status) => new()
    {
        Id = id,
        AgentId = agentId,
        CommandType = CommandType.Script,
        Payload = "echo 1",
        Status = status
    };

    private sealed class FakeReportRepo(AutomationExecutionReport report) : IAutomationExecutionReportRepository
    {
        public AutomationExecutionReport Single { get; } = report;

        public Task<AutomationExecutionReport?> GetByIdAsync(Guid id)
            => Task.FromResult<AutomationExecutionReport?>(Single.Id == id ? Single : null);

        public Task<bool> MarkCancelledAsync(Guid executionId, string? errorMessage, DateTime cancelledAt)
        {
            if (Single.Id != executionId
                || Single.Status is AutomationExecutionStatus.Completed
                    or AutomationExecutionStatus.Failed
                    or AutomationExecutionStatus.Cancelled)
            {
                return Task.FromResult(false);
            }

            Single.Status = AutomationExecutionStatus.Cancelled;
            Single.ErrorMessage = errorMessage;
            Single.ResultReceivedAt = cancelledAt;
            return Task.FromResult(true);
        }

        public Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport value) => throw new NotSupportedException();
        public Task<AutomationExecutionReport?> GetByCommandIdAsync(Guid commandId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByAgentIdAsync(Guid agentId, int limit = 100, AutomationExecutionStatus? status = null, AutomationExecutionSourceType? sourceType = null, Guid? taskId = null, Guid? scriptId = null, string? correlationId = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutomationExecutionReport>> GetByTaskIdAsync(Guid taskId, int limit = 100) => throw new NotSupportedException();
        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId) => throw new NotSupportedException();
        public Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt) => throw new NotSupportedException();
        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId) => throw new NotSupportedException();
    }

    private sealed class FakeCommandRepo(params AgentCommand[] commands) : ICommandRepository
    {
        private readonly Dictionary<Guid, AgentCommand> _commands = commands.ToDictionary(c => c.Id);
        public List<Guid> Updated { get; } = [];
        /// <summary>Comando único do fake — falha alto se o cenário criar mais de um.</summary>
        public AgentCommand Single => _commands.Values.Single();

        public Task<AgentCommand?> GetByIdAsync(Guid id)
            => Task.FromResult(_commands.TryGetValue(id, out var command) ? command : null);

        public Task UpdateStatusAsync(Guid id, CommandStatus status, string? result, int? exitCode, string? errorMessage)
        {
            if (_commands.TryGetValue(id, out var command))
            {
                command.Status = status;
                command.Result = result;
                command.ExitCode = exitCode;
                command.ErrorMessage = errorMessage;
                Updated.Add(id);
            }
            return Task.CompletedTask;
        }

        public Task<IEnumerable<AgentCommand>> GetPendingByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
        public Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(Guid agentId, int limit = 50) => throw new NotSupportedException();
        public Task<AgentCommand> CreateAsync(AgentCommand command) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentCommand>> GetRedeliveryCandidatesAsync(IReadOnlyCollection<Guid> agentIds, DateTime createdAfterUtc, DateTime staleBeforeUtc, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentCommand>> GetExpiredUnconfirmedAsync(DateTime createdBeforeUtc, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
