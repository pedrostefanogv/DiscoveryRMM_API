using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Automation;

public sealed record SyncAutomationPolicyCommand(Guid AgentId, object? Request, string? CorrelationId, string? Username, string? IpAddress) : ICommand<Result<object>>;
public sealed record GetAgentCommandsQuery(Guid AgentId, int Limit = 50) : IQuery<Result<object>>;

/// <summary>Payload do ack enviado pelo agent (POST me/automation/executions/{commandId}/ack).</summary>
public sealed record AckAutomationExecutionCommand(Guid AgentId, Guid CommandId, object? Request, string? CorrelationId) : ICommand<Result<VoidResult>>
{
    public Guid? TaskId { get; init; }
    public Guid? ScriptId { get; init; }
    public int SourceType { get; init; }
    public string? MetadataJson { get; init; }
}

/// <summary>Payload do resultado enviado pelo agent (POST me/automation/executions/{commandId}/result).</summary>
public sealed record CompleteAutomationExecutionCommand(Guid AgentId, Guid CommandId, object? Request, string? CorrelationId) : ICommand<Result<VoidResult>>
{
    public Guid? TaskId { get; init; }
    public Guid? ScriptId { get; init; }
    public int SourceType { get; init; }
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? MetadataJson { get; init; }
}