using Discovery.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Services;

public sealed record RemoteDebugInboundLogEntry(
    Guid SessionId,
    Guid AgentId,
    string? Message,
    string? Level,
    DateTime? TimestampUtc,
    long? Sequence);

public interface IRemoteDebugLogRelay
{
    Task RelayAsync(RemoteDebugInboundLogEntry entry, string transport, CancellationToken cancellationToken = default);
}

public sealed class RemoteDebugLogRelayService : IRemoteDebugLogRelay
{
    private readonly IRemoteDebugSessionManager _sessionManager;
    private readonly IHubContext<RemoteDebugHub> _hubContext;
    private readonly ILogger<RemoteDebugLogRelayService> _logger;

    public RemoteDebugLogRelayService(
        IRemoteDebugSessionManager sessionManager,
        IHubContext<RemoteDebugHub> hubContext,
        ILogger<RemoteDebugLogRelayService> logger)
    {
        _sessionManager = sessionManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task RelayAsync(RemoteDebugInboundLogEntry entry, string transport, CancellationToken cancellationToken = default)
    {
        if (entry.SessionId == Guid.Empty || entry.AgentId == Guid.Empty)
            return;

        if (!_sessionManager.TryGetSessionForAgent(entry.SessionId, entry.AgentId, out _))
        {
            _logger.LogDebug(
                "Ignoring remote debug log for unauthorized or expired session. SessionId={SessionId}, AgentId={AgentId}, Transport={Transport}",
                entry.SessionId,
                entry.AgentId,
                transport);
            return;
        }

        var safeMessage = string.IsNullOrWhiteSpace(entry.Message) ? string.Empty : entry.Message.TrimEnd();
        if (safeMessage.Length > 4096)
            safeMessage = safeMessage[..4096];

        var normalizedLevel = string.IsNullOrWhiteSpace(entry.Level)
            ? "info"
            : entry.Level.Trim().ToLowerInvariant();

        var sequence = entry.Sequence ?? _sessionManager.NextSequence(entry.SessionId);

        await _hubContext.Clients
            .Group(RemoteDebugGroupNames.ForSession(entry.SessionId))
            .SendAsync("RemoteDebugLog", new
            {
                sessionId = entry.SessionId,
                agentId = entry.AgentId,
                level = normalizedLevel,
                message = safeMessage,
                timestampUtc = entry.TimestampUtc ?? DateTime.UtcNow,
                sequence,
                transport
            }, cancellationToken: cancellationToken);

        _sessionManager.Touch(entry.SessionId);
    }
}