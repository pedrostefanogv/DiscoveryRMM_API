namespace Discovery.Core.Interfaces;

/// <summary>
/// Publica eventos de patch no canal de dashboard (NATS → WS) para atualizar o
/// console sem esperar o próximo polling. Nunca propaga falha de publicação.
/// </summary>
public interface IPatchEventPublisher
{
    Task PublishScanCompletedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        int pendingCount,
        int installedCount,
        bool pendingReboot,
        string scanStatus,
        CancellationToken ct = default);

    Task PublishInstallCompletedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        int installedCount,
        int failedCount,
        bool rebootRequired,
        CancellationToken ct = default);

    Task PublishApprovalChangedAsync(
        Guid agentId,
        string hostname,
        Guid? clientId,
        Guid? siteId,
        string updateKey,
        string action,
        CancellationToken ct = default);
}
