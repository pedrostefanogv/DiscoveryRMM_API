using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Software;

public sealed record GetAgentSoftwareQuery(Guid AgentId) : IQuery<Result<object>>;
public sealed record ReportAgentSoftwareCommand(
    Guid AgentId, DateTime? CollectedAt, object? Software
) : ICommand<Result<VoidResult>>;