using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

/// <summary>Resposta do mini questionário do template exposta no detalhe do chamado.</summary>
public sealed record TicketAnswerDto(
    string QuestionKey,
    string QuestionLabel,
    string? ValueText,
    string ValueJson,
    DateTime CreatedAt);

public record TicketFilterQuery(
    Guid? ClientId = null,
    Guid? SiteId = null,
    Guid? AgentId = null,
    Guid? DepartmentId = null,
    Guid? WorkflowProfileId = null,
    Guid? WorkflowStateId = null,
    Guid? AssignedToUserId = null,
    TicketPriority? Priority = null,
    bool? SlaBreached = null,
    bool? IsClosed = null,
    string? Text = null,
    string? Cursor = null,
    int Limit = 100,
    // Offset não é mais suportado. Use Cursor para paginação cursor-based.
    int Offset = 0,
    bool HasGlobalAccess = true,
    IReadOnlyList<Guid>? AllowedClientIds = null,
    IReadOnlyList<Guid>? AllowedSiteIds = null,
    // Usado pelo KPI (recorte temporal). Ignorado pela listagem.
    DateTime? Since = null,
    // Filtros do mini questionário do template.
    Guid? TemplateId = null,
    string? AnswerKey = null,
    string? AnswerValue = null,
    TicketAnswerMatch AnswerMatch = TicketAnswerMatch.Exact
);

public record CreateTicketSavedViewRequest(
    string Name,
    Guid? UserId = null,
    bool IsShared = false,
    TicketFilterQuery? Filter = null
);

public record UpdateTicketSavedViewRequest(
    string Name,
    bool IsShared = false,
    TicketFilterQuery? Filter = null
);

/// <summary>Resultado agregado de KPIs operacionais do módulo de tickets.</summary>
public record TicketKpiResult(
    int TotalOpen,
    int TotalClosed,
    int SlaBreached,
    int SlaWarning,
    int OnHold,
    double FrtAchievementRate,
    double AvgResolutionHours,
    double AvgAgeOpenHours,
    IReadOnlyList<TicketKpiByAssignee> ByAssignee,
    IReadOnlyList<TicketKpiByDepartment> ByDepartment
);

public record TicketKpiByAssignee(
    Guid? AssignedToUserId,
    int Open,
    int Breached
);

public record TicketKpiByDepartment(
    Guid? DepartmentId,
    int Open,
    int Breached
);

// ─── Fase 4 ──────────────────────────────────────────────────────────────────

public record AddTicketWatcherRequest(Guid UserId);

public record DraftKbArticleRequest(bool PersistAsDraft = false);

public record KbLinkFeedbackRequest(bool Useful);
