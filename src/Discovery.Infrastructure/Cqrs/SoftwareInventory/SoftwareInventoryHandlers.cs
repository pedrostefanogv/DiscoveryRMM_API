using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.SoftwareInventory.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.SoftwareInventory;

public sealed class ListAgentSoftwareQueryHandler(ISoftwareInventoryService svc) : IRequestHandler<ListAgentSoftwareQuery, Result<SoftwareInventoryDto>>
{
    public async Task<Result<SoftwareInventoryDto>> Handle(ListAgentSoftwareQuery q, CancellationToken ct)
    {
        var current = await svc.GetCurrentByAgentIdAsync(q.AgentId, ct);
        var snapshots = await svc.GetSnapshotsByAgentIdAsync(q.AgentId, ct);
        var items = current.Select(s => new SoftwareItemDto(s.InventoryId, s.Name, s.Version, s.Publisher, s.InstallDate?.ToString("o"), s.CollectedAt)).ToList().AsReadOnly();
        var snap = snapshots.FirstOrDefault();
        var snapDto = snap is not null ? new SnapshotDto(snap.AgentId, snap.TotalInstalled, snap.LastCollectedAt) : null;
        return Result<SoftwareInventoryDto>.Success(new SoftwareInventoryDto(items, snapDto));
    }
}

public sealed class GetSoftwareInventorySnapshotQueryHandler : IRequestHandler<GetSoftwareInventorySnapshotQuery, Result<SnapshotDto>>
{
    public Task<Result<SnapshotDto>> Handle(GetSoftwareInventorySnapshotQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<SnapshotDto>.Success(new SnapshotDto(Guid.Empty, 0, null)));
    }
}