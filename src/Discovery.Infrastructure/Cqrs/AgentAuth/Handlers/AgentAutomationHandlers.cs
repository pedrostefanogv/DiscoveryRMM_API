using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Opções compartilhadas para desserializar os payloads que o agent envia
/// dentro do wrapper <c>request</c> (ack/result/policy-sync).
/// Os enums do agent trafegam como string (ex.: <c>"ForceSync"</c>) — sem o
/// <see cref="JsonStringEnumConverter"/> o System.Text.Json lança JsonException
/// e o endpoint responde 500. O converter também aceita números, preservando
/// compatibilidade com payloads antigos.
/// </summary>
internal static class AgentCallbackJson
{
    /// <summary>Instância única (thread-safe após configuração).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class SyncAutomationPolicyHandler(
    IAutomationTaskService taskService
) : IRequestHandler<SyncAutomationPolicyCommand, Result<object>>
{
    // Reutiliza a mesma instância de JsonSerializerOptions (thread-safe após configuração)
    // em vez de alocar uma nova a cada chamada de policy-sync (chamada a cada ~5 min por agent).
    private static readonly JsonSerializerOptions SyncJsonOptions = AgentCallbackJson.Options;

    public async Task<Result<object>> Handle(SyncAutomationPolicyCommand cmd, CancellationToken ct)
    {
        // Desserializa o request vindo do agent (JsonElement → AgentAutomationPolicySyncRequest)
        var syncRequest = DeserializeSyncRequest(cmd.Request);

        var response = await taskService.SyncPolicyForAgentAsync(
            cmd.AgentId,
            syncRequest,
            cmd.Username,
            cmd.IpAddress,
            cmd.CorrelationId ?? Guid.NewGuid().ToString("N"),
            ct);

        return Result<object>.Success(response);
    }

    private static AgentAutomationPolicySyncRequest DeserializeSyncRequest(object? request)
    {
        if (request is JsonElement json)
        {
            return JsonSerializer.Deserialize<AgentAutomationPolicySyncRequest>(
                json.GetRawText(),
                SyncJsonOptions) ?? new AgentAutomationPolicySyncRequest();
        }

        return new AgentAutomationPolicySyncRequest();
    }
}

public sealed class GetAgentCommandsHandler(
    IAutomationTaskRepository taskRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<GetAgentCommandsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentCommandsQuery q, CancellationToken ct)
    {
        // Resolve agent's hierarchical scope (SiteId + ClientId)
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        Guid? siteId = agent?.SiteId;
        Guid? clientId = null;
        if (siteId.HasValue)
        {
            var site = await siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }

        var tasks = await taskRepo.GetActiveTasksForAgentAsync(
            agentId: q.AgentId,
            agentSiteId: siteId,
            siteClientId: clientId,
            limit: q.Limit);

        return Result<object>.Success(new { commands = tasks });
    }
}

public sealed class AckAutomationExecutionHandler(
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<AckAutomationExecutionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(AckAutomationExecutionCommand cmd, CancellationToken ct)
    {
        var payload = DeserializeAck(cmd.Request, cmd);
        var sourceType = MapSourceType((int)payload.SourceType);

        // Upsert PRIMEIRO: execuções automáticas (sem comando dispatchado pelo
        // servidor) ainda não têm registro. O UpdateAck só grava metadados e
        // publica o evento de ack quando a linha já existe, então invertendo a
        // ordem o payload do ack (stage/task/trigger/política PSADT) deixa de
        // ser descartado no caminho normal iniciado pelo agent.
        await reportRepo.UpsertPolicyExecutionAsync(
            cmd.AgentId, cmd.CommandId, payload.TaskId, payload.ScriptId, sourceType,
            AutomationExecutionStatus.Acknowledged, cmd.CorrelationId);

        await reportRepo.UpdateAckAsync(
            cmd.CommandId,
            payload.TaskId,
            payload.ScriptId,
            payload.MetadataJson,
            DateTime.UtcNow,
            cmd.CorrelationId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static AutomationExecutionAckRequest DeserializeAck(object? request, AckAutomationExecutionCommand cmd)
    {
        if (request is JsonElement json)
        {
            return JsonSerializer.Deserialize<AutomationExecutionAckRequest>(json.GetRawText(), SyncJsonOptions)
                ?? new AutomationExecutionAckRequest();
        }

        // Fallback: propriedades já materializadas no comando (via `with`).
        return new AutomationExecutionAckRequest
        {
            TaskId = cmd.TaskId,
            ScriptId = cmd.ScriptId,
            SourceType = (AutomationExecutionSourceType)cmd.SourceType,
            MetadataJson = cmd.MetadataJson
        };
    }

    private static AutomationExecutionSourceType MapSourceType(int raw)
        => Enum.IsDefined(typeof(AutomationExecutionSourceType), raw)
            ? (AutomationExecutionSourceType)raw
            : AutomationExecutionSourceType.ForceSync;

    private static readonly JsonSerializerOptions SyncJsonOptions = AgentCallbackJson.Options;
}

public sealed class CompleteAutomationExecutionHandler(
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<CompleteAutomationExecutionCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(CompleteAutomationExecutionCommand cmd, CancellationToken ct)
    {
        var payload = DeserializeResult(cmd.Request, cmd);
        var sourceType = MapSourceType((int)payload.SourceType);

        // Upsert PRIMEIRO, com status não-terminal: garante a linha para que o
        // UpdateResult grave exit code / erro / metadados (senão, quando o result
        // chega antes de qualquer ack, o payload era descartado e o evento do
        // dashboard não era publicado). O status terminal é aplicado no
        // UpdateResult, que também é a guarda de idempotência.
        await reportRepo.UpsertPolicyExecutionAsync(
            cmd.AgentId, cmd.CommandId, payload.TaskId, payload.ScriptId, sourceType,
            AutomationExecutionStatus.Dispatched, cmd.CorrelationId);

        await reportRepo.UpdateResultAsync(
            cmd.CommandId,
            payload.TaskId,
            payload.ScriptId,
            payload.Success,
            payload.ExitCode,
            payload.ErrorMessage,
            payload.MetadataJson,
            DateTime.UtcNow,
            cmd.CorrelationId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static AutomationExecutionResultRequest DeserializeResult(object? request, CompleteAutomationExecutionCommand cmd)
    {
        if (request is JsonElement json)
        {
            return JsonSerializer.Deserialize<AutomationExecutionResultRequest>(json.GetRawText(), SyncJsonOptions)
                ?? new AutomationExecutionResultRequest();
        }

        return new AutomationExecutionResultRequest
        {
            TaskId = cmd.TaskId,
            ScriptId = cmd.ScriptId,
            SourceType = (AutomationExecutionSourceType)cmd.SourceType,
            Success = cmd.Success,
            ExitCode = cmd.ExitCode,
            ErrorMessage = cmd.ErrorMessage,
            MetadataJson = cmd.MetadataJson
        };
    }

    private static AutomationExecutionSourceType MapSourceType(int raw)
        => Enum.IsDefined(typeof(AutomationExecutionSourceType), raw)
            ? (AutomationExecutionSourceType)raw
            : AutomationExecutionSourceType.ForceSync;

    private static readonly JsonSerializerOptions SyncJsonOptions = AgentCallbackJson.Options;
}