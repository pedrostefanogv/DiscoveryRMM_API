using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentUpdates.Queries;

namespace Discovery.Core.Cqrs.AgentUpdates.Commands;

public sealed record RefreshAgentBuildCommand(
    string Version, string Platform, string Architecture,
    string ArtifactType, string FileName, string ContentType,
    Stream Content, string? SignatureThumbprint, string? CommitHash, string? Actor
) : ICommand<Result<AgentBuildDto>>;

public sealed record ForceAgentUpdateCommand(
    Guid AgentId, string? Version, string? Channel
) : ICommand<Result<VoidResult>>;

public sealed record SyncAgentRepositoryCommand(
    string Source, string? Branch
) : ICommand<Result<VoidResult>>;

public sealed record SyncAndBuildAgentCommand(
    string Source, string? Branch
) : ICommand<Result<VoidResult>>;

public sealed record RebuildAgentCommand(string? Actor = null) : ICommand<Result<AgentBuildDto>>;
