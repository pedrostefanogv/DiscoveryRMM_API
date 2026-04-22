using System.Text.Json;
using NATS.Client.Core;

namespace Discovery.Api.Services;

public sealed class RemoteDebugNatsBridgeService : BackgroundService
{
    private readonly NatsConnection _natsConnection;
    private readonly IRemoteDebugLogRelay _remoteDebugLogRelay;
    private readonly ILogger<RemoteDebugNatsBridgeService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RemoteDebugNatsBridgeService(
        NatsConnection natsConnection,
        IRemoteDebugLogRelay remoteDebugLogRelay,
        ILogger<RemoteDebugNatsBridgeService> logger)
    {
        _natsConnection = natsConnection;
        _remoteDebugLogRelay = remoteDebugLogRelay;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BridgeSubjectAsync("tenant.*.site.*.agent.*.remote-debug.log", stoppingToken);
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

                await _remoteDebugLogRelay.RelayAsync(
                    new RemoteDebugInboundLogEntry(
                        payload.SessionId,
                        effectiveAgentId.Value,
                        payload.Message,
                        payload.Level,
                        payload.TimestampUtc,
                        payload.Sequence),
                    RemoteDebugTransportNames.Nats,
                    stoppingToken);
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
