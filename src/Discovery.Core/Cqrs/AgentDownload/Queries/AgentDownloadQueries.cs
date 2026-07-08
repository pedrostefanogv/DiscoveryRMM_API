using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentDownload.Queries;

public sealed record GetAgentDownloadQuery(Guid AgentId, string? Platform, string? Architecture) : IQuery<Result<AgentDownloadDto>>;
public sealed record AgentDownloadDto(string DownloadUrl, string Version, string FileName, string? Sha256);