using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.Misc;

public sealed record GetAgentIdentityQuery : IQuery<Result<object>>;
public sealed record GetAppStoreEffectiveQuery(string InstallationType) : IQuery<Result<object>>;
public sealed record GetRuntimeCustomFieldsQuery(Guid? TaskId, Guid? ScriptId) : IQuery<Result<object>>;
public sealed record UpsertCollectedCustomFieldCommand(object Request) : ICommand<Result<object>>;
public sealed record IssueZeroTouchDeployTokenCommand : ICommand<Result<object>>;
public sealed record GetAgentUpdateManifestQuery(string? CurrentVersion, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record DownloadAgentUpdateQuery(Guid? ReleaseId, string? Version, string? Platform, string? Architecture, string? ArtifactType) : IQuery<Result<object>>;
public sealed record ReportAgentUpdateCommand(object Request) : ICommand<Result<object>>;