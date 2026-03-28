using System.Text.Json;
using Meduza.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;

namespace Meduza.Api.Services;

public sealed class RemoteDebugNatsBridgeService : BackgroundService
{
    private readonly NatsConnection _natsConnection;
    private readonly IRemoteDebugSessionManager _sessionManager;
    private readonly IHubContext<RemoteDebugHub> _hubContext;
    private readonly ILogger<RemoteDebugNatsBridgeService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RemoteDebugNatsBridgeService(
        NatsConnection natsConnection,
        IRemoteDebugSessionManager sessionManager,
        IHubContext<RemoteDebugHub> hubContext,
        ILogger<RemoteDebugNatsBridgeService> logger)
    {
        _natsConnection = natsConnection;
        _sessionManager = sessionManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var legacyTask = BridgeSubjectAsync("agent.*.remote-debug.log", stoppingToken);
        var tenantTask = BridgeSubjectAsync("tenant.*.site.*.agent.*.remote-debug.log", stoppingToken);
        await Task.WhenAll(legacyTask, tenantTask);
    }

    private async Task BridgeSubjectAsync(string subjectPattern, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Remote debug NATS bridge subscribing: {SubjectPattern}", subjectPattern);

        await foreach (var msg in _natsConnection.SubscribeAsync<string>(subjectPattern, cancellationToken: stoppingToken))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<RemoteDebugLogMessage>(msg.Data ?? string.Empty, JsonOptions);
                if (payload is null || payload.SessionId == Guid.Empty)
                    continue;

                var subjectAgentId = TryExtractAgentId(msg.Subject);
                var effectiveAgentId = payload.AgentId ?? subjectAgentId;
                if (!effectiveAgentId.HasValue)
                    continue;

                if (!_sessionManager.TryGetSessionForAgent(payload.SessionId, effectiveAgentId.Value, out _))
                    continue;

                var safeMessage = string.IsNullOrWhiteSpace(payload.Message) ? string.Empty : payload.Message.TrimEnd();
                if (safeMessage.Length > 4096)
                    safeMessage = safeMessage[..4096];

                var normalizedLevel = string.IsNullOrWhiteSpace(payload.Level)
                    ? "info"
                    : payload.Level.Trim().ToLowerInvariant();

                var sequence = payload.Sequence ?? _sessionManager.NextSequence(payload.SessionId);

                await _hubContext.Clients
                    .Group(RemoteDebugGroupNames.ForSession(payload.SessionId))
                    .SendAsync("RemoteDebugLog", new
                    {
                        sessionId = payload.SessionId,
                        agentId = effectiveAgentId,
                        level = normalizedLevel,
                        message = safeMessage,
                        timestampUtc = payload.TimestampUtc ?? DateTime.UtcNow,
                        sequence,
                        transport = "nats"
                    }, cancellationToken: stoppingToken);

                _sessionManager.Touch(payload.SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error relaying remote debug log from NATS subject {Subject}", msg.Subject);
            }
        }
    }

    private static Guid? TryExtractAgentId(string subject)
    {
        var parts = subject.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // agent.{agentId}.remote-debug.log
        if (parts.Length >= 2 && string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(parts[1], out var legacyAgentId))
                return legacyAgentId;
        }

        // tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log
        var agentIndex = Array.FindIndex(parts, p => string.Equals(p, "agent", StringComparison.OrdinalIgnoreCase));
        if (agentIndex >= 0 && parts.Length > agentIndex + 1 && Guid.TryParse(parts[agentIndex + 1], out var tenantAgentId))
            return tenantAgentId;

        return null;
    }

    private sealed class RemoteDebugLogMessage
    {
        public Guid SessionId { get; set; }
        public Guid? AgentId { get; set; }
        public string? Message { get; set; }
        public string? Level { get; set; }
        public DateTime? TimestampUtc { get; set; }
        public long? Sequence { get; set; }
    }
}
