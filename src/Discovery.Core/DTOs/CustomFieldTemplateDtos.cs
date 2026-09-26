using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public sealed record CustomFieldTemplateDto(
    Guid Id,
    Guid? ClientId,
    Guid? DepartmentId,
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    IReadOnlyList<string> Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    bool DefaultIsRequired,
    bool IsBuiltIn,
    bool IsActive,
    int SortOrder,
    string? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpsertCustomFieldTemplateInput(
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
    int SortOrder);
