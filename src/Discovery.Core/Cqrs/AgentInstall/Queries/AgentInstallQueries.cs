using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentInstall.Queries;

public sealed record GetAgentInstallUrlQuery(Guid ClientId, Guid SiteId, string? Platform, string? Architecture) : IQuery<Result<AgentInstallDto>>;
public sealed record AgentInstallDto(string DownloadUrl, string? InstallerCommand, string? DeployToken);