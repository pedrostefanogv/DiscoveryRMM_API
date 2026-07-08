using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Configuration.Commands;

/// <summary>
/// Command to update server configuration.
/// </summary>
public sealed record UpdateServerConfigCommand(
    string ConfigJson,
    string? UpdatedBy
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Command to update client configuration.
/// </summary>
public sealed record UpdateClientConfigCommand(
    Guid ClientId,
    string ConfigJson,
    string? UpdatedBy
) : ICommand<Result<VoidResult>>;
