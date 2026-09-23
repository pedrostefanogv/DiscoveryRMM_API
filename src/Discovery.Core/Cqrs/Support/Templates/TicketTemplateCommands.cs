using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Templates;

public sealed record CreateTicketTemplateCommand(
    Guid? ClientId, Guid? DepartmentId, string Name, string Title, string Description,
    string? Priority, string? Category, string CustomFieldDefaultsJson, bool IsActive,
    string? CreatedBy) : ICommand<Result<TicketTemplateDto>>;

public sealed record UpdateTicketTemplateCommand(
    Guid Id, Guid? ClientId, Guid? DepartmentId, string Name, string Title, string Description,
    string? Priority, string? Category, string CustomFieldDefaultsJson,
    bool IsActive) : ICommand<Result<TicketTemplateDto>>;

public sealed record DeleteTicketTemplateCommand(Guid Id) : ICommand<Result<VoidResult>>;
