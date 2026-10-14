using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Repositories;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão dos bugs encontrados ao revisar o callback de automação (14/10/2026):
///
/// 1. Um ack tardio regredia o status terminal — o agent mantém fila persistente
///    de callbacks e reenvia o ack depois do result quando a API volta, o que
///    rebaixava Completed/Failed para Acknowledged e permitia reprocessar o
///    result (evento/notificação duplicados).
/// 2. Os handlers chamavam o Update ANTES do upsert, então em execução iniciada
///    pelo agent (sem linha prévia) o payload de ack/result era descartado.
/// </summary>
[TestFixture]
public class AutomationExecutionCallbackOrderingTests
{
    [TestCase(AutomationExecutionStatus.Completed, false)]
    [TestCase(AutomationExecutionStatus.Failed, false)]
    [TestCase(AutomationExecutionStatus.Cancelled, false)]
    [TestCase(AutomationExecutionStatus.Dispatched, true)]
    [TestCase(AutomationExecutionStatus.Acknowledged, true)]
    public void TerminalGuard_BlocksOnlyTerminalStatuses(AutomationExecutionStatus status, bool expectedNotTerminal)
    {
        var predicate = AutomationExecutionReportRepository.NotTerminal.Compile();

        var report = new AutomationExecutionReport { Status = status };

        Assert.That(predicate(report), Is.EqualTo(expectedNotTerminal),
            "ack/result só podem alterar execuções não-terminais");
    }

    [Test]
    public async Task Ack_UpsertsBeforeUpdate()
    {
        var repo = new RecordingReportRepo();

        await Ack(repo);

        Assert.That(repo.Calls, Is.EqualTo(new[] { "upsert", "ack" }),
            "o upsert precisa criar a linha antes do UpdateAck gravar os metadados");
        Assert.That(repo.UpsertStatus, Is.EqualTo(AutomationExecutionStatus.Acknowledged));
        Assert.That(repo.AckMetadataJson, Is.EqualTo("{\"stage\":\"ack\"}"));
        Assert.That(repo.UpsertCommandId, Is.EqualTo(repo.AckCommandId));
    }

    [Test]
    public async Task Result_UpsertsNonTerminalPlaceholderBeforeUpdate()
    {
        var repo = new RecordingReportRepo();

        await Result(repo);

        Assert.That(repo.Calls, Is.EqualTo(new[] { "upsert", "result" }));
        Assert.That(repo.UpsertStatus, Is.EqualTo(AutomationExecutionStatus.Dispatched),
            "o upsert não pode gravar o status terminal, senão o UpdateResult (guarda de idempotência) descarta o payload");
        Assert.That(repo.ResultCommandId, Is.EqualTo(repo.UpsertCommandId));
        Assert.That(repo.ResultExitCode, Is.EqualTo(0));
        Assert.That(repo.ResultMetadataJson, Is.EqualTo("{\"stage\":\"result\"}"));
    }

    [Test]
    public async Task Result_WithStringSourceType_KeepsSourceTypeOnUpsert()
    {
        var repo = new RecordingReportRepo();

        await Result(repo, sourceType: "SoftwareUninstall", success: false);

        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.SoftwareUninstall));
        Assert.That(repo.ResultSuccess, Is.False);
    }

    private static Task Ack(RecordingReportRepo repo)
    {
        var cmd = new AckAutomationExecutionCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            RequestOf("""{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":"ForceSync","MetadataJson":"{\"stage\":\"ack\"}"}"""),
            "corr-ack");

        return new AckAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);
    }

    private static Task Result(RecordingReportRepo repo, string sourceType = "ForceSync", bool success = true)
    {
        var cmd = new CompleteAutomationExecutionCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            RequestOf(
                $$"""{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":"{{sourceType}}","Success":{{(success ? "true" : "false")}},"ExitCode":0,"MetadataJson":"{\"stage\":\"result\"}"}"""),
            "corr-result");

        return new CompleteAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);
    }

    private static JsonElement RequestOf(string requestJson)
        => JsonDocument.Parse(
            $$"""{"agentId":"9dddbd43-d487-4166-a28e-813c975be5b0","commandId":"0e439c37-32d4-4690-8263-0d5b10dce606","request":{{requestJson}},"correlationId":"corr-1"}""")
            .RootElement.GetProperty("request");

    private sealed class RecordingReportRepo : IAutomationExecutionReportRepository
    {
        public List<string> Calls { get; } = [];
        public Guid? UpsertCommandId { get; private set; }
        public Guid? AckCommandId { get; private set; }
        public Guid? ResultCommandId { get; private set; }
        public AutomationExecutionSourceType? UpsertSourceType { get; private set; }
        public AutomationExecutionStatus? UpsertStatus { get; private set; }
        public string? AckMetadataJson { get; private set; }
        public string? ResultMetadataJson { get; private set; }
        public int? ResultExitCode { get; private set; }
        public bool? ResultSuccess { get; private set; }

        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId)
        {
            Calls.Add("upsert");
            UpsertCommandId = commandId;
            UpsertSourceType = sourceType;
            UpsertStatus = status;
            return Task.CompletedTask;
        }

        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId)
        {
            Calls.Add("ack");
            AckCommandId = commandId;
            AckMetadataJson = ackMetadataJson;
            return Task.CompletedTask;
        }

        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId)
        {
            Calls.Add("result");
            ResultCommandId = commandId;
            ResultSuccess = success;
            ResultExitCode = exitCode;
            ResultMetadataJson = resultMetadataJson;
            return Task.CompletedTask;
        }

        public Task<AutomationExecutionReport> CreateAsync(AutomationExecutionReport report) => throw new NotSupportedException();
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
        public Task UpdateResultFromCommandAsync(Guid commandId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt) => throw new NotSupportedException();
    }
}
