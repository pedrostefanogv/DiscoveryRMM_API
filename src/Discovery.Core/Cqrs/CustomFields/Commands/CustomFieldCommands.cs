using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.CustomFields.Commands;

public sealed record CreateCustomFieldCommand(string Name, string Label, string? Description, CustomFieldScopeType ScopeType, CustomFieldDataType DataType, bool IsRequired, bool IsSecret, string? OptionsJson, string? ValidationRegex, Guid? DepartmentId, string? UpdatedBy) : ICommand<Result<CustomFieldDto>>;
public sealed record UpdateCustomFieldCommand(Guid Id, string? Name, string? Label, string? Description, bool? IsRequired, bool? IsSecret, string? OptionsJson, string? ValidationRegex, Guid? DepartmentId, bool? IsActive, string? UpdatedBy) : ICommand<Result<CustomFieldDto>>;
public sealed record DeactivateCustomFieldCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record CustomFieldDto(Guid Id, string Name, string Label, string? Description, string ScopeType, string DataType, bool IsRequired, bool IsSecret, bool IsActive, Guid? DepartmentId, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record UpsertCustomFieldValueCommand(
    Guid DefinitionId,
    CustomFieldScopeType ScopeType,
    Guid? EntityId,
    string ValueJson,
    string? UpdatedBy
) : ICommand<Result<CustomFieldResolvedValueDto>>;