using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug de 13-14/10/2026 no homolog: o agent envia o callback dentro
/// do wrapper <c>{agentId, commandId, request, correlationId}</c> e o
/// <c>SourceType</c> como STRING (ex.: <c>"ForceSync"</c>). O handler
/// re-desserializava o <c>request</c> (JsonElement) com opções sem
/// <c>JsonStringEnumConverter</c>, o que lançava JsonException e devolvia 500 em
/// todo POST /ack e /result — execuções automáticas nunca eram registradas.
/// Os payloads abaixo são o formato literal produzido pelo agent Go.
/// </summary>
[TestFixture]
public class AgentAutomationCallbackPayloadTests
{
    private static JsonElement RequestOf(string wrapperJson)
        => JsonDocument.Parse(wrapperJson).RootElement.GetProperty("request");

    private static string AgentWrapper(string requestJson, string correlationId = "corr-1")
        => $$"""
        {
          "agentId": "9dddbd43-d487-4166-a28e-813c975be5b0",
          "commandId": "0e439c37-32d4-4690-8263-0d5b10dce606",
          "request": {{requestJson}},
          "correlationId": "{{correlationId}}"
        }
        """;

    // Formato real quando a task não tem script: ScriptId é omitido (omitempty).
    private const string AckSkipJson =
        """{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":"ForceSync","MetadataJson":"{\"stage\":\"skip\"}"}""";

    // Mesmo payload, agora com ScriptId (é o que produz BytePositionInLine 123 no log).
    private const string AckWithScriptJson =
        """{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","ScriptId":"9e90b3e1-c540-465b-8b13-b557fbf302e4","SourceType":"AgentManual","MetadataJson":"{\"stage\":\"ack\"}"}""";

    [Test]
    public async Task Ack_WithStringSourceType_DeserializesInsteadOfThrowing()
    {
        var repo = new CapturingReportRepo();
        var agentId = Guid.NewGuid();
        var commandId = Guid.NewGuid();

        var cmd = new AckAutomationExecutionCommand(
            agentId, commandId, RequestOf(AgentWrapper(AckSkipJson)), "corr-1");

        var result = await new AckAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, "ack com SourceType string deve ser aceito");
        Assert.That(repo.AckCommandId, Is.EqualTo(commandId));
        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.ForceSync));
        Assert.That(repo.UpsertStatus, Is.EqualTo(AutomationExecutionStatus.Acknowledged));
        Assert.That(repo.UpsertTaskId, Is.EqualTo(Guid.Parse("01a136e4-0884-75c6-853b-f08b2d70208c")));
    }

    [Test]
    public async Task Ack_WithScriptAndAgentManualSource_PreservesSourceType()
    {
        var repo = new CapturingReportRepo();

        var cmd = new AckAutomationExecutionCommand(
            Guid.NewGuid(), Guid.NewGuid(), RequestOf(AgentWrapper(AckWithScriptJson)), "corr-2");

        var result = await new AckAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.AgentManual));
        Assert.That(repo.UpsertScriptId, Is.EqualTo(Guid.Parse("9e90b3e1-c540-465b-8b13-b557fbf302e4")));
    }

    [Test]
    public async Task Ack_WithNumericSourceType_StillWorks()
    {
        var repo = new CapturingReportRepo();

        var cmd = new AckAutomationExecutionCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            RequestOf(AgentWrapper("""{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":2}""")),
            null);

        var result = await new AckAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, "compatibilidade com payload numérico deve ser mantida");
        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.ForceSync));
    }

    [Test]
    public async Task Result_WithStringSourceType_DeserializesInsteadOfThrowing()
    {
        var repo = new CapturingReportRepo();
        var commandId = Guid.NewGuid();

        var cmd = new CompleteAutomationExecutionCommand(
            Guid.NewGuid(), commandId,
            RequestOf(AgentWrapper(
                """{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":"SoftwareUpdate","Success":true,"ExitCode":0,"MetadataJson":"{}"}""")),
            "corr-3");

        var result = await new CompleteAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, "result com SourceType string deve ser aceito");
        Assert.That(repo.ResultCommandId, Is.EqualTo(commandId));
        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.SoftwareUpdate));
        // O status terminal é aplicado pelo UpdateResult (a ordem upsert→update é
        // coberta em AutomationExecutionCallbackOrderingTests).
        Assert.That(repo.ResultExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task Result_Failure_WithStringSourceType_IsAccepted()
    {
        var repo = new CapturingReportRepo();

        var cmd = new CompleteAutomationExecutionCommand(
            Guid.NewGuid(), Guid.NewGuid(),
            RequestOf(AgentWrapper(
                """{"TaskId":"01a136e4-0884-75c6-853b-f08b2d70208c","SourceType":"Scheduled","Success":false,"ErrorMessage":"timeout"}""")),
            null);

        var result = await new CompleteAutomationExecutionHandler(repo).Handle(cmd, CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.UpsertSourceType, Is.EqualTo(AutomationExecutionSourceType.Scheduled));
        Assert.That(repo.UpsertStatus, Is.EqualTo(AutomationExecutionStatus.Dispatched),
            "o upsert grava só o placeholder; o desfecho (Completed/Failed) vem do UpdateResult");
    }

    private sealed class CapturingReportRepo : IAutomationExecutionReportRepository
    {
        public Guid? AckCommandId { get; private set; }
        public Guid? ResultCommandId { get; private set; }
        public Guid? UpsertTaskId { get; private set; }
        public Guid? UpsertScriptId { get; private set; }
        public AutomationExecutionSourceType? UpsertSourceType { get; private set; }
        public AutomationExecutionStatus? UpsertStatus { get; private set; }
        public int? ResultExitCode { get; private set; }

        public Task UpdateAckAsync(Guid commandId, Guid? taskId, Guid? scriptId, string? ackMetadataJson, DateTime acknowledgedAt, string? correlationId)
        {
            AckCommandId = commandId;
            return Task.CompletedTask;
        }

        public Task UpdateResultAsync(Guid commandId, Guid? taskId, Guid? scriptId, bool success, int? exitCode, string? errorMessage, string? resultMetadataJson, DateTime resultReceivedAt, string? correlationId)
        {
            ResultCommandId = commandId;
            ResultExitCode = exitCode;
            return Task.CompletedTask;
        }

        public Task UpsertPolicyExecutionAsync(Guid agentId, Guid commandId, Guid? taskId, Guid? scriptId, AutomationExecutionSourceType sourceType, AutomationExecutionStatus status, string? correlationId)
        {
            UpsertTaskId = taskId;
            UpsertScriptId = scriptId;
            UpsertSourceType = sourceType;
            UpsertStatus = status;
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
