using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.CustomFieldTemplates;

// Contrato plano (igual a TicketTemplateCommands): o body do controller é o
// próprio payload do modelo, sem wrapper { input: ... }.

public sealed record CreateCustomFieldTemplateCommand(
    Guid? ClientId,
    Guid? DepartmentId,
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool DefaultIsRequired,
    bool IsActive,
    int SortOrder,
    string? CreatedBy) : ICommand<Result<CustomFieldTemplateDto>>;

public sealed record UpdateCustomFieldTemplateCommand(
    Guid Id,
    Guid? ClientId,
    Guid? DepartmentId,
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool DefaultIsRequired,
    bool IsActive,
    int SortOrder) : ICommand<Result<CustomFieldTemplateDto>>;

public sealed record DeleteCustomFieldTemplateCommand(Guid Id) : ICommand<Result<VoidResult>>;
