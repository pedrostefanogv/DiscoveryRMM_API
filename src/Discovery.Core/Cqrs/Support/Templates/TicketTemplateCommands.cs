using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Templates;

public sealed record CreateTicketTemplateCommand(
    Guid? ClientId, Guid? DepartmentId, string Name, string Title, string Description,
    string? Priority, string? Category, string CustomFieldDefaultsJson, string? QuestionsJson,
    bool IsActive, string? CreatedBy) : ICommand<Result<TicketTemplateDto>>;

public sealed record UpdateTicketTemplateCommand(
    Guid Id, Guid? ClientId, Guid? DepartmentId, string Name, string Title, string Description,
    string? Priority, string? Category, string CustomFieldDefaultsJson, string? QuestionsJson,
    bool IsActive) : ICommand<Result<TicketTemplateDto>>;

/// <summary>
/// Exclui um template. Sem <paramref name="Force"/>, recusa quando o template já
/// foi usado por chamados (devolve Conflict com a contagem).
/// </summary>
public sealed record DeleteTicketTemplateCommand(Guid Id, bool Force = false) : ICommand<Result<VoidResult>>;
