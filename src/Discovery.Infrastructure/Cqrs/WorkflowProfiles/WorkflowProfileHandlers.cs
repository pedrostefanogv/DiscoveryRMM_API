using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.WorkflowProfiles.Commands;
using Discovery.Core.Cqrs.WorkflowProfiles.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.WorkflowProfiles;

public sealed class ListWorkflowProfilesQueryHandler(IWorkflowProfileService svc) : IRequestHandler<ListWorkflowProfilesQuery, Result<IReadOnlyList<WorkflowProfileDto>>>
{
    public async Task<Result<IReadOnlyList<WorkflowProfileDto>>> Handle(ListWorkflowProfilesQuery q, CancellationToken ct)
    {
        var profiles = await svc.GetByClientAsync(q.ClientId, q.IncludeGlobal, ct);
        return Result<IReadOnlyList<WorkflowProfileDto>>.Success(profiles.Select(Map).ToList().AsReadOnly());
    }
    private static WorkflowProfileDto Map(WorkflowProfile p) => new(p.Id, p.ClientId, p.DepartmentId, p.Name, p.Description, p.SlaHours == 0 ? null : p.SlaHours, p.SlaCalendarId, p.FirstResponseSlaHours == 0 ? null : p.FirstResponseSlaHours, p.DefaultPriority.ToString(), p.IsActive, p.CreatedAt, p.UpdatedAt);
}

public sealed class GetWorkflowProfileByIdQueryHandler(IWorkflowProfileService svc) : IRequestHandler<GetWorkflowProfileByIdQuery, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(GetWorkflowProfileByIdQuery q, CancellationToken ct)
    {
        var p = await svc.GetByIdAsync(q.Id, ct);
        if (p is null) return Result<WorkflowProfileDto>.Failure(Error.NotFound($"Profile {q.Id} not found"));
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(p.Id, p.ClientId, p.DepartmentId, p.Name, p.Description, p.SlaHours, p.SlaCalendarId, p.FirstResponseSlaHours, p.DefaultPriority.ToString(), p.IsActive, p.CreatedAt, p.UpdatedAt));
    }
}

public sealed class CreateWorkflowProfileCommandHandler(IWorkflowProfileService svc) : IRequestHandler<CreateWorkflowProfileCommand, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(CreateWorkflowProfileCommand cmd, CancellationToken ct)
    {
        TicketPriority dp = TicketPriority.Medium;
        if (cmd.DefaultPriority is not null && Enum.TryParse(cmd.DefaultPriority, true, out TicketPriority tp)) dp = tp;
        var p = new WorkflowProfile { ClientId = cmd.ClientId, DepartmentId = cmd.DepartmentId, Name = cmd.Name, Description = cmd.Description, SlaHours = cmd.SlaHours ?? 24, SlaCalendarId = cmd.SlaCalendarId, FirstResponseSlaHours = cmd.FirstResponseSlaHours ?? 4, DefaultPriority = dp, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(p, ct);
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(created.Id, created.ClientId, created.DepartmentId, created.Name, created.Description, created.SlaHours, created.SlaCalendarId, created.FirstResponseSlaHours, created.DefaultPriority.ToString(), created.IsActive, created.CreatedAt, created.UpdatedAt));
    }
}

public sealed class UpdateWorkflowProfileCommandHandler(IWorkflowProfileService svc) : IRequestHandler<UpdateWorkflowProfileCommand, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(UpdateWorkflowProfileCommand cmd, CancellationToken ct)
    {
        var p = await svc.GetByIdAsync(cmd.Id, ct);
        if (p is null) return Result<WorkflowProfileDto>.Failure(Error.NotFound($"Profile {cmd.Id} not found"));
        if (cmd.Name is not null) p.Name = cmd.Name;
        if (cmd.Description is not null) p.Description = cmd.Description;
        if (cmd.SlaHours.HasValue) p.SlaHours = cmd.SlaHours.Value;
        if (cmd.SlaCalendarId is not null) p.SlaCalendarId = cmd.SlaCalendarId;
        if (cmd.FirstResponseSlaHours.HasValue) p.FirstResponseSlaHours = cmd.FirstResponseSlaHours.Value;
        if (cmd.DefaultPriority is not null && Enum.TryParse(cmd.DefaultPriority, true, out TicketPriority tp)) p.DefaultPriority = tp;
        if (cmd.IsActive.HasValue) p.IsActive = cmd.IsActive.Value;
        p.UpdatedAt = DateTime.UtcNow;
        var updated = await svc.UpdateAsync(p, ct);
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(updated.Id, updated.ClientId, updated.DepartmentId, updated.Name, updated.Description, updated.SlaHours, updated.SlaCalendarId, updated.FirstResponseSlaHours, updated.DefaultPriority.ToString(), updated.IsActive, updated.CreatedAt, updated.UpdatedAt));
    }
}

public sealed class DeleteWorkflowProfileCommandHandler(IWorkflowProfileService svc) : IRequestHandler<DeleteWorkflowProfileCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteWorkflowProfileCommand cmd, CancellationToken ct)
    {
        var ok = await svc.DeleteAsync(cmd.Id, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"Profile {cmd.Id} not found"));
    }
}