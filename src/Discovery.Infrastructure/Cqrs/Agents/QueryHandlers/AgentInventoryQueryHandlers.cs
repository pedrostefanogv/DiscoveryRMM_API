using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.DTOs;
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
            hardware.BiosVersion,
            hardware.BiosManufacturer,
            hardware.BiosDate,
            hardware.TotalMemoryBytes,
            hardware.ProcessorCores,
            hardware.ProcessorThreads,
            hardware.Processor,
            hardware.ProcessorArchitecture,
            hardware.ProcessorFrequencyGhz,
            hardware.MachineScore,
            hardware.GpuModel,
            hardware.GpuMemoryBytes,
            hardware.OsName,
            hardware.OsVersion,
            hardware.OsArchitecture
        ));
    }
}

public sealed class GetAgentSoftwareQueryHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareQuery, Result<CursorPageDto<AgentSoftwareItemDto>>>
{
    public async Task<Result<CursorPageDto<AgentSoftwareItemDto>>> Handle(GetAgentSoftwareQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<CursorPageDto<AgentSoftwareItemDto>>.Failure(Error.NotFound("Agent not found."));

        var limit = Math.Clamp(q.Limit, 1, 500);
        var page = await softwareRepo.GetCurrentByAgentIdPagedAsync(q.AgentId, q.Cursor, limit + 1, q.Search, q.Descending);

        var hasMore = page.Count > limit;
        var items = (hasMore ? page.Take(limit) : page)
            .Select(s => new AgentSoftwareItemDto(
                s.InventoryId,
                s.Name,
                s.Version,
                s.Publisher,
                s.Source,
                s.InstallId,
                s.Serial,
                s.InstallDate,
                s.CollectedAt))
            .ToList().AsReadOnly();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = page[limit];
            nextCursor = last.InventoryId.ToString();
        }

        return Result<CursorPageDto<AgentSoftwareItemDto>>.Success(
            new CursorPageDto<AgentSoftwareItemDto>(items, items.Count, q.Cursor, nextCursor, hasMore, q.Limit));
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