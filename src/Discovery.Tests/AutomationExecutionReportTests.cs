using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Persistência do report de execução: o correlation id da requisição precisa
/// ser gravado (senão o histórico/CSV de operações perde o vínculo com a
/// operação disparada na UI).
/// </summary>
[TestFixture]
public class AutomationExecutionReportTests
{
    private static AgentCommand NewCommand() => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        CommandType = CommandType.Script,
        Payload = "echo 1"
    };

    [Test]
    public async Task CreateReportAsync_PersistsTrimmedCorrelationId()
    {
        var repo = new CapturingReportRepo();

        await RunAutomationTaskCommandHandler.CreateReportAsync(
            repo, NewCommand(), taskId: null, scriptId: null,
            AutomationExecutionSourceType.RunNow, new { mode = "task-run-now" }, "  corr-123  ");

        Assert.That(repo.Created, Has.Count.EqualTo(1));
        Assert.That(repo.Created[0].CorrelationId, Is.EqualTo("corr-123"));
        Assert.That(repo.Created[0].Status, Is.EqualTo(AutomationExecutionStatus.Dispatched));
        Assert.That(repo.Created[0].SourceType, Is.EqualTo(AutomationExecutionSourceType.RunNow));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task CreateReportAsync_BlankCorrelationIsStoredAsNull(string? correlationId)
    {
        var repo = new CapturingReportRepo();

        await RunAutomationTaskCommandHandler.CreateReportAsync(
            repo, NewCommand(), taskId: null, scriptId: null,
            AutomationExecutionSourceType.ForceSync, new { mode = "force-sync" }, correlationId);

        Assert.That(repo.Created[0].CorrelationId, Is.Null);
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
