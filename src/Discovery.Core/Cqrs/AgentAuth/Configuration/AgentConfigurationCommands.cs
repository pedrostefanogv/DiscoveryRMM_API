using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Configuration;

public sealed record GetAgentConfigurationQuery : IQuery<Result<object>>;
public sealed record ReportAgentTlsMismatchCommand(string Target) : ICommand<Result<object>>;
public sealed record GetAgentSyncManifestQuery : IQuery<Result<object>>;