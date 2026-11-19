namespace Discovery.Core.Interfaces;

public sealed record TunnelTicketIssueResult(
    string Ticket,
    int ExpiresInSeconds,
    DateTime ExpiresAtUtc);

public sealed record TunnelTicketPrincipal(
    Guid UserId,
    Guid AgentId,
    Guid SessionId,
    DateTime ExpiresAtUtc);

/// <summary>
/// Emite e consome tickets opacos de uso único e TTL curto para autenticação
/// de túnel WebSocket (/tunnel/ws), evitando expor JWT de sessão na query string.
/// </summary>
public interface ITunnelTicketService
{
    Task<TunnelTicketIssueResult> IssueAsync(
        Guid userId,
        Guid agentId,
        Guid sessionId,
        CancellationToken ct = default);

    Task<TunnelTicketPrincipal?> ConsumeAsync(
        string ticket,
        Guid agentId,
        Guid sessionId,
        CancellationToken ct = default);
}
