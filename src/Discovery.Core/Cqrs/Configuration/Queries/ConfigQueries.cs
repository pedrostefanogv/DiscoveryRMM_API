using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Configuration.Queries;

/// <summary>
/// Query to get server configuration.
/// </summary>
public sealed record GetServerConfigQuery : IQuery<Result<ServerConfigDto>>;

public sealed record ServerConfigDto(
    string ConfigJson,
    int Version,
    DateTime UpdatedAt
);

/// <summary>
/// Query to get client configuration.
/// </summary>
public sealed record GetClientConfigQuery(
    Guid ClientId
) : IQuery<Result<ClientConfigDto>>;

public sealed record ClientConfigDto(
    Guid ClientId,
    string ConfigJson,
    int Version,
    DateTime UpdatedAt
);
