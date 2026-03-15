using Meduza.Core.Enums;

namespace Meduza.Core.Interfaces;

public interface ISyncInvalidationPublisher
{
    Task PublishGlobalAsync(
        SyncResourceType resource,
        string reason,
        AppInstallationType? installationType = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task PublishByScopeAsync(
        SyncResourceType resource,
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        string reason,
        AppInstallationType? installationType = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
