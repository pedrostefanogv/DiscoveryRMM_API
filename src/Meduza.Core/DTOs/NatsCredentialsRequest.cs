namespace Meduza.Core.DTOs;

public record NatsCredentialsRequest(
    Guid? ClientId,
    Guid? SiteId);
