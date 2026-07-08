using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentHardwareQueryHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareQuery, Result<AgentHardwareDto>>
{
    public async Task<Result<AgentHardwareDto>> Handle(GetAgentHardwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentHardwareDto>.Failure(Error.NotFound("Agent not found."));

        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);
        if (hardware is null)
            return Result<AgentHardwareDto>.Failure(Error.NotFound("Hardware info not found."));

        return Result<AgentHardwareDto>.Success(new AgentHardwareDto(
            hardware.Manufacturer ?? string.Empty,
            hardware.Model ?? string.Empty,
            hardware.SerialNumber,
            null, // BiosVersion
            null, // TotalRamMb
            hardware.ProcessorCores,
            hardware.Processor,
            null, // OsName
            null  // OsVersion
        ));
    }
}

public sealed class GetAgentSoftwareQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareQuery, Result<IReadOnlyList<AgentSoftwareItemDto>>>
{
    public async Task<Result<IReadOnlyList<AgentSoftwareItemDto>>> Handle(GetAgentSoftwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<IReadOnlyList<AgentSoftwareItemDto>>.Failure(Error.NotFound("Agent not found."));

        var limit = Math.Clamp(q.Limit, 1, 500);
        var page = await softwareRepo.GetCurrentByAgentIdPagedAsync(q.AgentId, q.Cursor, limit + 1, q.Search, q.Descending);

        var hasMore = page.Count > limit;
        var items = hasMore ? page.Take(limit).ToList() : page.ToList();
        var dtos = items.Select(s => new AgentSoftwareItemDto(s.Name, s.Version, s.Publisher, s.InstallDate)).ToList();
        return Result<IReadOnlyList<AgentSoftwareItemDto>>.Success(dtos);
    }
}

public sealed class GetAgentSoftwareSnapshotQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareSnapshotQuery, Result<AgentSoftwareSnapshotDto>>
{
    public async Task<Result<AgentSoftwareSnapshotDto>> Handle(GetAgentSoftwareSnapshotQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<AgentSoftwareSnapshotDto>.Failure(Error.NotFound("Agent not found."));

        var snapshot = await softwareRepo.GetSnapshotByAgentIdAsync(q.AgentId);
        return Result<AgentSoftwareSnapshotDto>.Success(new AgentSoftwareSnapshotDto(
            q.AgentId, snapshot?.TotalInstalled ?? 0, snapshot?.LastCollectedAt));
    }
}