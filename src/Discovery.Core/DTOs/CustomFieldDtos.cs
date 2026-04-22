using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public sealed record CustomFieldAccessBindingInput(
    Guid? TaskId,
    Guid? ScriptId,
    bool CanRead,
    bool CanWrite);

public sealed record UpsertCustomFieldDefinitionInput(
    string Name,
    string Label,
    string? Description,
    CustomFieldScopeType ScopeType,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsActive,
    bool IsSecret,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool AllowRuntimeRead,
    bool AllowAgentWrite,
    CustomFieldRuntimeAccessMode RuntimeAccessMode,
    IReadOnlyList<CustomFieldAccessBindingInput>? AccessBindings);

public sealed record UpsertCustomFieldValueInput(
    Guid DefinitionId,
    CustomFieldScopeType ScopeType,
    Guid? EntityId,
    string ValueJson,
    string? UpdatedBy);

public sealed record AgentCustomFieldCollectedValueInput(
    Guid? DefinitionId,
    string? Name,
    string ValueJson,
    Guid? TaskId,
    Guid? ScriptId,
    string? UpdatedBy);

public sealed record CustomFieldResolvedValueDto(
    Guid DefinitionId,
    string Name,
    string Label,
    CustomFieldScopeType ScopeType,
    Guid? EntityId,
    string ValueJson,
    DateTime UpdatedAt,
    bool IsSecret);

public sealed record RuntimeCustomFieldDto(
    Guid DefinitionId,
    string Name,
    string Label,
    CustomFieldScopeType ScopeType,
    string ValueJson,
    bool IsSecret);

public sealed record CustomFieldAccessBindingDto(
    Guid? TaskId,
    Guid? ScriptId,
    bool CanRead,
    bool CanWrite);

public sealed record CustomFieldSchemaItemDto(
    Guid DefinitionId,
    string Name,
    string Label,
    string? Description,
    CustomFieldScopeType ScopeType,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsActive,
    bool IsSecret,
    IReadOnlyList<string> Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool AllowRuntimeRead,
    bool AllowAgentWrite,
    CustomFieldRuntimeAccessMode RuntimeAccessMode,
    IReadOnlyList<CustomFieldAccessBindingDto> AccessBindings,
    Guid? EntityId,
    string ValueJson,
    DateTime? ValueUpdatedAt);
