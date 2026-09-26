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
/// Soft delete: marca <see cref="Discovery.Core.Entities.TicketTemplate.DeletedAt"/>.
/// O template sai da listagem, mas a linha permanece para permitir restauração —
/// o histórico dos chamados (template_name) não é afetado.
/// </summary>
public sealed record DeleteTicketTemplateCommand(Guid Id, string? DeletedBy = null) : ICommand<Result<VoidResult>>;

/// <summary>
/// Exclusão física (purge) da lixeira. Sem <paramref name="Force"/>, recusa
/// quando o template já foi usado por chamados (devolve Conflict com a
/// contagem); com Force, a FK ON DELETE SET NULL preserva o histórico
/// (tickets.template_name).
/// </summary>
public sealed record PurgeTicketTemplateCommand(Guid Id, bool Force = false) : ICommand<Result<VoidResult>>;

/// <summary>
/// Tira o template da lixeira (limpa DeletedAt/DeletedBy). Não altera IsActive:
/// quem estava inativo volta inativo — ativar é ação própria.
/// </summary>
public sealed record RestoreTicketTemplateCommand(Guid Id) : ICommand<Result<VoidResult>>;
