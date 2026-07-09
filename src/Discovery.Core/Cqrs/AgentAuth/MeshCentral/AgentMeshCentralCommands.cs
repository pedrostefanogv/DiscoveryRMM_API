using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.MeshCentral;

public sealed record CreateMeshCentralEmbedUrlCommand(Guid AgentId, int? ViewMode, string? HideMask, string? MeshNodeId, string? GotoDeviceName) : ICommand<Result<object>>;
public sealed record GetMeshCentralInstallQuery(Guid AgentId) : IQuery<Result<object>>;