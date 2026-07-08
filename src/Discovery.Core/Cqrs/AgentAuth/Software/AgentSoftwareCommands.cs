using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Software;

public sealed record GetAgentSoftwareQuery : IQuery<Result<object>>;
public sealed record ReportAgentSoftwareCommand(
    DateTime? CollectedAt, object? Software
) : ICommand<Result<VoidResult>>;