namespace Discovery.Core.DTOs;

public record NatsCredentialsRequest(
    Guid? ClientId,
    Guid? SiteId);
