using Meduza.Core.Enums;

namespace Meduza.Core.DTOs;

public class SyncInvalidationPingDto
{
    public Guid EventId { get; set; }
    public Guid AgentId { get; set; }
    public string EventType { get; set; } = "sync.invalidated";
    public SyncResourceType Resource { get; set; }
    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public AppInstallationType? InstallationType { get; set; }
    public string Revision { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? CorrelationId { get; set; }
}
