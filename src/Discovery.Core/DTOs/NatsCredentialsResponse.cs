namespace Discovery.Core.DTOs;

public record NatsCredentialsResponse(
    string Jwt,
    string NkeySeed,
    string PublicKey,
    DateTime ExpiresAtUtc,
    IReadOnlyList<string> PublishSubjects,
    IReadOnlyList<string> SubscribeSubjects);
