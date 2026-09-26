using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.WorkflowProfiles.Commands;
using Discovery.Core.Cqrs.WorkflowProfiles.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.WorkflowProfiles;

/// <summary>
/// Garante que o calendário vinculado seja global ou do mesmo cliente do perfil.
/// Sem isto, um perfil do cliente A podia apontar para o calendário do cliente B.
/// </summary>
internal static class WorkflowProfileCalendarRules
{
    internal static async Task<Error?> ValidateScopeAsync(
        ISlaCalendarService calendars, Guid? calendarId, Guid? profileClientId, CancellationToken ct)
    {
        if (calendarId is null) return null;

        var calendar = await calendars.GetByIdAsync(calendarId.Value, ct);
        if (calendar is null)
            return Error.Validation("slaCalendarId", "Calendário de SLA não encontrado.");

        if (calendar.ClientId.HasValue && calendar.ClientId != profileClientId)
            return Error.Validation("slaCalendarId", "O calendário pertence a outro cliente e não pode ser usado neste perfil.");

        return null;
    }
}

public sealed class ListWorkflowProfilesQueryHandler(IWorkflowProfileService svc) : IRequestHandler<ListWorkflowProfilesQuery, Result<IReadOnlyList<WorkflowProfileDto>>>
{
    public async Task<Result<IReadOnlyList<WorkflowProfileDto>>> Handle(ListWorkflowProfilesQuery q, CancellationToken ct)
    {
        var profiles = await svc.GetByClientAsync(q.ClientId, q.IncludeGlobal, ct);
        return Result<IReadOnlyList<WorkflowProfileDto>>.Success(profiles.Select(Map).ToList().AsReadOnly());
    }
    private static WorkflowProfileDto Map(WorkflowProfile p) => new(p.Id, p.ClientId, p.DepartmentId, p.Name, p.Description, p.SlaHours == 0 ? null : p.SlaHours, p.SlaCalendarId, p.FirstResponseSlaHours == 0 ? null : p.FirstResponseSlaHours, p.DefaultPriority.ToString(), p.IsActive, p.CreatedAt, p.UpdatedAt, p.SlaWarningPercent);
}

public sealed class GetWorkflowProfileByIdQueryHandler(IWorkflowProfileService svc) : IRequestHandler<GetWorkflowProfileByIdQuery, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(GetWorkflowProfileByIdQuery q, CancellationToken ct)
    {
        var p = await svc.GetByIdAsync(q.Id, ct);
        if (p is null) return Result<WorkflowProfileDto>.Failure(Error.NotFound($"Profile {q.Id} not found"));
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(p.Id, p.ClientId, p.DepartmentId, p.Name, p.Description, p.SlaHours, p.SlaCalendarId, p.FirstResponseSlaHours, p.DefaultPriority.ToString(), p.IsActive, p.CreatedAt, p.UpdatedAt, p.SlaWarningPercent));
    }
}

public sealed class CreateWorkflowProfileCommandHandler(IWorkflowProfileService svc, ISlaCalendarService calendars) : IRequestHandler<CreateWorkflowProfileCommand, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(CreateWorkflowProfileCommand cmd, CancellationToken ct)
    {
        var scopeError = await WorkflowProfileCalendarRules.ValidateScopeAsync(calendars, cmd.SlaCalendarId, cmd.ClientId, ct);
        if (scopeError is not null) return Result<WorkflowProfileDto>.Failure(scopeError);

        TicketPriority dp = TicketPriority.Medium;
        if (cmd.DefaultPriority is not null && Enum.TryParse(cmd.DefaultPriority, true, out TicketPriority tp)) dp = tp;
        var p = new WorkflowProfile { ClientId = cmd.ClientId, DepartmentId = cmd.DepartmentId, Name = cmd.Name, Description = cmd.Description, SlaHours = cmd.SlaHours ?? 24, SlaCalendarId = cmd.SlaCalendarId, SlaWarningPercent = cmd.SlaWarningPercent, FirstResponseSlaHours = cmd.FirstResponseSlaHours ?? 4, DefaultPriority = dp, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var created = await svc.CreateAsync(p, ct);
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(created.Id, created.ClientId, created.DepartmentId, created.Name, created.Description, created.SlaHours, created.SlaCalendarId, created.FirstResponseSlaHours, created.DefaultPriority.ToString(), created.IsActive, created.CreatedAt, created.UpdatedAt, created.SlaWarningPercent));
    }
}

public sealed class UpdateWorkflowProfileCommandHandler(IWorkflowProfileService svc, ISlaCalendarService calendars) : IRequestHandler<UpdateWorkflowProfileCommand, Result<WorkflowProfileDto>>
{
    public async Task<Result<WorkflowProfileDto>> Handle(UpdateWorkflowProfileCommand cmd, CancellationToken ct)
    {
        var p = await svc.GetByIdAsync(cmd.Id, ct);
        if (p is null) return Result<WorkflowProfileDto>.Failure(Error.NotFound($"Profile {cmd.Id} not found"));

        // O cliente do perfil não muda no update; valida o calendário efetivo.
        var effectiveCalendarId = cmd.ClearSlaCalendar == true ? null : (cmd.SlaCalendarId ?? p.SlaCalendarId);
        var scopeError = await WorkflowProfileCalendarRules.ValidateScopeAsync(calendars, effectiveCalendarId, p.ClientId, ct);
        if (scopeError is not null) return Result<WorkflowProfileDto>.Failure(scopeError);
        if (cmd.Name is not null) p.Name = cmd.Name;
        if (cmd.Description is not null) p.Description = cmd.Description;
        if (cmd.SlaHours.HasValue) p.SlaHours = cmd.SlaHours.Value;
        // "ClearSlaCalendar" distingue "não enviado" de "voltar para 24x7".
        if (cmd.ClearSlaCalendar == true) p.SlaCalendarId = null;
        else if (cmd.SlaCalendarId is not null) p.SlaCalendarId = cmd.SlaCalendarId;
        if (cmd.FirstResponseSlaHours.HasValue) p.FirstResponseSlaHours = cmd.FirstResponseSlaHours.Value;
        // Nulo significa "usar o padrão (80%)": o cliente sempre envia o campo.
        p.SlaWarningPercent = cmd.SlaWarningPercent;
        if (cmd.DefaultPriority is not null && Enum.TryParse(cmd.DefaultPriority, true, out TicketPriority tp)) p.DefaultPriority = tp;
        if (cmd.IsActive.HasValue) p.IsActive = cmd.IsActive.Value;
        p.UpdatedAt = DateTime.UtcNow;
        var updated = await svc.UpdateAsync(p, ct);
        return Result<WorkflowProfileDto>.Success(new WorkflowProfileDto(updated.Id, updated.ClientId, updated.DepartmentId, updated.Name, updated.Description, updated.SlaHours, updated.SlaCalendarId, updated.FirstResponseSlaHours, updated.DefaultPriority.ToString(), updated.IsActive, updated.CreatedAt, updated.UpdatedAt, updated.SlaWarningPercent));
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