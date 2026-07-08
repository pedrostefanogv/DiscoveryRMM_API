using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.MeshCentral;

public sealed record CreateMeshCentralEmbedUrlCommand(int? ViewMode, string? HideMask, string? MeshNodeId, string? GotoDeviceName) : ICommand<Result<object>>;
public sealed record GetMeshCentralInstallQuery : IQuery<Result<object>>;