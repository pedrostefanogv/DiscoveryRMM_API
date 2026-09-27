using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Assignments;

/// <summary>Perfil + métricas da equipe do departamento para a tela de configuração.</summary>
public sealed record GetDepartmentAssignmentTeamMetricsQuery(Guid DepartmentId)
    : IQuery<Result<IReadOnlyList<DepartmentMemberProfileDto>>>;

/// <summary>Última decisão da triagem por IA registrada para o chamado (404 quando não há).</summary>
public sealed record GetTicketAssignmentDecisionQuery(Guid TicketId)
    : IQuery<Result<TicketAssignmentDecisionDto>>;

/// <summary>Auditoria paginada das decisões da triagem por IA.</summary>
public sealed record ListTicketAssignmentDecisionsQuery(
    Guid? DepartmentId = null,
    Guid? TicketId = null,
    Guid? ChosenUserId = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 50)
    : IQuery<Result<IReadOnlyList<TicketAssignmentDecisionDto>>>;

/// <summary>Atualiza competências/capacidade do membro usadas pela triagem por IA.</summary>
public sealed record UpdateDepartmentMemberProfileCommand(
    Guid DepartmentId,
    Guid UserId,
    IReadOnlyList<string>? SkillTags = null,
    int? SkillLevel = null,
    int? MaxOpenTickets = null,
    decimal? Weight = null,
    bool? AcceptsAiAssignment = null,
    bool ClearMaxOpenTickets = false)
    : ICommand<Result<DepartmentMemberProfileDto>>;

/// <summary>Força o recálculo dos snapshots de métricas (departamento ou global).</summary>
public sealed record RefreshTechnicianMetricsCommand(Guid? DepartmentId = null) : ICommand<Result<int>>;

/// <summary>Executa a triagem por IA e devolve a sugestão, sem atribuir.</summary>
public sealed record PreviewTicketAssignmentCommand(Guid TicketId, Guid? TriggeredByUserId = null)
    : ICommand<Result<TicketAssignmentResultDto>>;

/// <summary>Executa a triagem por IA e aplica o responsável escolhido.</summary>
public sealed record ApplyTicketAssignmentCommand(Guid TicketId, Guid? TriggeredByUserId = null)
    : ICommand<Result<TicketAssignmentResultDto>>;
