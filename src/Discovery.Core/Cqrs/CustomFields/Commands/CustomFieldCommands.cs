using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.CustomFields.Commands;

/// <summary>
/// Vínculo de execução (tarefa ou script) informado na criação/edição da definição.
/// TaskId/ScriptId são mutuamente exclusivos na prática: ao menos um deve ser informado.
/// </summary>
public sealed record CustomFieldAccessBindingCommand(Guid? TaskId, Guid? ScriptId, bool CanRead, bool CanWrite);

public sealed record CreateCustomFieldCommand(
    string Name,
    string Label,
    string? Description,
    CustomFieldScopeType ScopeType,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsSecret,
    string? OptionsJson,
    string? ValidationRegex,
    Guid? DepartmentId,
    string? UpdatedBy,
    bool AllowRuntimeRead = false,
    bool AllowAgentWrite = false,
    CustomFieldRuntimeAccessMode RuntimeAccessMode = CustomFieldRuntimeAccessMode.Disabled,
    IReadOnlyList<CustomFieldAccessBindingCommand>? AccessBindings = null,
    bool AllowWebRead = true,
    bool AllowWebEdit = true,
    bool IsPinned = false,
    // A tela envia a lista de opções (Dropdown/ListBox); OptionsJson continua aceito.
    IReadOnlyList<string>? Options = null,
    string? DefaultValueJson = null) : ICommand<Result<CustomFieldDto>>;

public sealed record UpdateCustomFieldCommand(
    Guid Id,
    string? Name,
    string? Label,
    string? Description,
    bool? IsRequired,
    bool? IsSecret,
    string? OptionsJson,
    string? ValidationRegex,
    Guid? DepartmentId,
    bool? IsActive,
    string? UpdatedBy,
    // Capacidades de automação: null = preservar o valor atual (patch).
    bool? AllowRuntimeRead = null,
    bool? AllowAgentWrite = null,
    CustomFieldRuntimeAccessMode? RuntimeAccessMode = null,
    // null = preservar vínculos atuais; lista vazia = remover todos; lista com itens = substituir.
    IReadOnlyList<CustomFieldAccessBindingCommand>? AccessBindings = null,
    // Visibilidade no portal: null = preservar (patch).
    bool? AllowWebRead = null,
    bool? AllowWebEdit = null,
    bool? IsPinned = null,
    // null = preservar as opções atuais.
    IReadOnlyList<string>? Options = null,
    // null = preservar o valor padrão; string vazia ou "null" = limpar.
    string? DefaultValueJson = null) : ICommand<Result<CustomFieldDto>>;

public sealed record DeactivateCustomFieldCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record CustomFieldDto(
    Guid Id,
    string Name,
    string Label,
    string? Description,
    string ScopeType,
    string DataType,
    bool IsRequired,
    bool IsSecret,
    bool IsActive,
    Guid? DepartmentId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? DepartmentName = null,
    bool AllowRuntimeRead = false,
    bool AllowAgentWrite = false,
    CustomFieldRuntimeAccessMode RuntimeAccessMode = CustomFieldRuntimeAccessMode.Disabled,
    IReadOnlyList<CustomFieldAccessBindingDto>? AccessBindings = null,
    bool AllowWebRead = true,
    bool AllowWebEdit = true,
    bool IsPinned = false,
    IReadOnlyList<string>? Options = null,
    string? DefaultValueJson = null);

public sealed record UpsertCustomFieldValueCommand(
    Guid DefinitionId,
    CustomFieldScopeType ScopeType,
    Guid? EntityId,
    string ValueJson,
    string? UpdatedBy
) : ICommand<Result<CustomFieldResolvedValueDto>>;
