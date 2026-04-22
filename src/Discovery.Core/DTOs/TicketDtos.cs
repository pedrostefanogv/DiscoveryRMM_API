using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

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
    int Limit = 100,
    int Offset = 0
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
