using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Eventos de patch no canal de dashboard. Best-effort: falha de NATS/contrato é
/// logada e nunca derruba o fluxo principal (upload/resultado/aprovação).
/// </summary>
public sealed class PatchEventPublisher(
    IAgentMessaging messaging,
    ILogger<PatchEventPublisher>? logger = null) : IPatchEventPublisher
{
    public Task PublishScanCompletedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        int pendingCount,
        int installedCount,
        bool pendingReboot,
        string scanStatus,
        CancellationToken ct = default)
        => PublishAsync("PatchScanCompleted", new
        {
            agentId,
            hostname,
            pendingCount,
            installedCount,
            pendingReboot,
            status = scanStatus
        }, clientId, siteId, ct);

    public Task PublishInstallCompletedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        int installedCount,
        int failedCount,
        bool rebootRequired,
        CancellationToken ct = default)
        => PublishAsync("PatchInstallCompleted", new
        {
            agentId,
            hostname,
            installedCount,
            failedCount,
            rebootRequired
        }, clientId, siteId, ct);

    public Task PublishApprovalChangedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        string updateKey,
        string action,
        CancellationToken ct = default)
        => PublishAsync("PatchApprovalChanged", new
        {
            agentId,
            hostname,
            updateKey,
            action
        }, clientId, siteId, ct);

    private async Task PublishAsync(
        string eventType,
        object data,
        Guid? clientId,
        Guid? siteId,
        CancellationToken ct)
    {
        try
        {
            if (!messaging.IsConnected)
                return;

            var message = DashboardEventMessage.Create(eventType, data, clientId, siteId);
            await messaging.PublishDashboardEventAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Falha ao publicar evento de patch {EventType}", eventType);
        }
    }
}
