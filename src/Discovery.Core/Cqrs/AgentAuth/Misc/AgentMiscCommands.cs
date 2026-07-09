using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Misc;

public sealed record GetAgentIdentityQuery(Guid AgentId) : IQuery<Result<object>>;
public sealed record GetAppStoreEffectiveQuery(Guid AgentId, string InstallationType) : IQuery<Result<object>>;
public sealed record GetRuntimeCustomFieldsQuery(Guid AgentId, Guid? TaskId, Guid? ScriptId) : IQuery<Result<object>>;
public sealed record UpsertCollectedCustomFieldCommand(Guid AgentId, object Request) : ICommand<Result<object>>;
public sealed record IssueZeroTouchDeployTokenCommand(Guid AgentId) : ICommand<Result<object>>;
public sealed record GetAgentUpdateManifestQuery(Guid AgentId, string? CurrentVersion, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record DownloadAgentUpdateQuery(Guid AgentId, Guid? ReleaseId, string? Version, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record ReportAgentUpdateCommand(Guid AgentId, object Request) : ICommand<Result<object>>;