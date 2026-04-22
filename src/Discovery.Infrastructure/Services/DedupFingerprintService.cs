using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public class DedupFingerprintService : IDedupFingerprintService
{
    private readonly IMonitoringEventNormalizationService _normalizationService;

    public DedupFingerprintService(IMonitoringEventNormalizationService normalizationService)
    {
        _normalizationService = normalizationService;
    }

    public string BuildDedupKey(AgentMonitoringEvent monitoringEvent, AutoTicketRule rule)
    {
        var normalizedPayload = _normalizationService.NormalizePayloadJson(monitoringEvent.PayloadJson);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPayload));
        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
        var windowMinutes = Math.Max(1, rule.DedupWindowMinutes);
        var bucket = DateTimeOffset.UtcNow;
        if (monitoringEvent.OccurredAt != default)
            bucket = new DateTimeOffset(DateTime.SpecifyKind(monitoringEvent.OccurredAt, DateTimeKind.Utc));

        var timeBucket = bucket.ToUnixTimeSeconds() / (windowMinutes * 60L);
        var siteSegment = monitoringEvent.SiteId?.ToString("N") ?? "none";

        return string.Join(':',
            monitoringEvent.ClientId.ToString("N"),
            siteSegment,
            monitoringEvent.AgentId.ToString("N"),
            monitoringEvent.AlertCode.Trim().ToLowerInvariant(),
            hashText,
            timeBucket.ToString());
    }
}