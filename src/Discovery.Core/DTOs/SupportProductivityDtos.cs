namespace Discovery.Core.DTOs;

public sealed record TicketMacroDto(
    Guid Id, Guid? ClientId, Guid? DepartmentId, string Name, string? Description,
    string Content, bool IsActive, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record TicketTemplateDto(
    Guid Id, Guid? ClientId, Guid? DepartmentId, string Name, string Title, string Description,
    string? Priority, string? Category, string CustomFieldDefaultsJson, string QuestionsJson,
    bool IsActive, string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt,
    DateTime? DeletedAt = null, string? DeletedBy = null);

public sealed record NotificationChannelDto(
    Guid Id, string Name, string Type, bool IsActive, string EventsJson, string ConfigJson,
    string? CreatedBy, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record DepartmentMemberDto(
    Guid Id, Guid DepartmentId, Guid UserId, string? UserName, bool IsActive, DateTime CreatedAt);

public sealed record TicketCsatGroupDto(string? Id, string Label, int Count, double Average);

public sealed record TicketCsatSummaryDto(
    int Total, int Rated, double Average,
    IReadOnlyDictionary<int, int> Distribution,
    IReadOnlyList<TicketCsatGroupDto> ByDepartment,
    IReadOnlyList<TicketCsatGroupDto> ByTechnician,
    // CSAT por máquina (hostname do agent). Avaliações feitas pelo usuário na
    // aba Suporte do agent gravam RatedBy = hostname, então o agrupamento por
    // técnico não as contempla; este recorte responde "como cada máquina
    // avalia o atendimento".
    IReadOnlyList<TicketCsatGroupDto> ByHostname);
