namespace Discovery.Core.DTOs;

/// <summary>
/// Envelope canonico para comando em massa (fan-out) publicado em *.agents.command.
/// </summary>
public record CommandDispatchEnvelope
{
    public Guid DispatchId { get; init; }
    public Guid? CommandId { get; init; }
    public string CommandType { get; init; } = string.Empty;
    public string TargetScope { get; init; } = string.Empty;
    public Guid? TargetClientId { get; init; }
    public Guid? TargetSiteId { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public string IdempotencyKey { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
}
