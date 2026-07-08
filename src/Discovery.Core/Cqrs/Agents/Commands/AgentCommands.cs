using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Commands;

/// <summary>
/// Command to refresh/upload a new agent build.
/// </summary>
public sealed record RefreshAgentBuildCommand(
    string Version,
    string Platform,
    string Architecture,
    string ArtifactType,
    string FileName,
    string ContentType,
    Stream Content,
    string? SignatureThumbprint,
    string? Actor
) : ICommand<Result<AgentBuildResult>>;

public sealed record AgentBuildResult(
    Guid BuildId,
    string Version,
    string Sha256,
    DateTime PublishedAt
);

/// <summary>
/// Command to promote an agent build to current.
/// </summary>
public sealed record PromoteAgentBuildCommand(
    Guid BuildId,
    string Channel
) : ICommand<Result<AgentBuildResult>>;
