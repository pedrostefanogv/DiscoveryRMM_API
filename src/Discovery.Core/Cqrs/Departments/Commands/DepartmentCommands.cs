using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Departments.Commands;

public sealed record CreateDepartmentCommand(
    string Name, string? Description, Guid? ClientId,
    Guid? InheritFromGlobalId, int SortOrder
) : ICommand<Result<DepartmentDto>>;

public sealed record UpdateDepartmentCommand(
    Guid Id, string? Name, string? Description,
    Guid? InheritFromGlobalId, int? SortOrder, bool? IsActive
) : ICommand<Result<DepartmentDto>>;

public sealed record DeleteDepartmentCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record DepartmentDto(
    Guid Id, Guid? ClientId, string Name, string? Description,
    Guid? InheritFromGlobalId, int SortOrder, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt
);
