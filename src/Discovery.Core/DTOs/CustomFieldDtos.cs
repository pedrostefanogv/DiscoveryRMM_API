using System.Text.Json;
using System.Text.Json.Nodes;
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
    IReadOnlyList<CustomFieldAccessBindingInput>? AccessBindings,
    Guid? DepartmentId = null,
    bool AllowWebRead = true,
    bool AllowWebEdit = true,
    bool IsPinned = false,
    string? DefaultValueJson = null);

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

/// <summary>
/// Payload PLANO enviado pelo agent em POST /agent-auth/me/custom-fields/collected
/// (é o próprio corpo do request; sem wrapper). <see cref="Value"/> é JSON bruto para
/// preservar o tipo (string, número, booleano...) exatamente como o script publicou.
/// </summary>
public sealed record AgentCollectedCustomFieldValueRequest(
    Guid? DefinitionId,
    string? Name,
    JsonElement? Value,
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

/// <summary>
/// Item do endpoint de runtime no contrato que o agent Go consome via
/// json.RawMessage/FieldScopeType: <c>scopeType</c> é numérico e
/// <c>valueJson</c> vai como JSON bruto (não como string JSON).
/// </summary>
public sealed record AgentRuntimeCustomFieldDto(
    Guid DefinitionId,
    string Name,
    string Label,
    int ScopeType,
    JsonNode? ValueJson,
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
    DateTime? ValueUpdatedAt,
    bool AllowWebRead = true,
    bool AllowWebEdit = true,
    bool IsPinned = false,
    string? ValueUpdatedBy = null);
