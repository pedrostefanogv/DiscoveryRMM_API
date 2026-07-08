using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.MeshCentral.Queries;

public sealed record GetMeshCentralStatusQuery : IQuery<Result<MeshCentralStatusDto>>;
public sealed record MeshCentralStatusDto(bool Connected, string? ServerUrl, DateTime? LastSync);