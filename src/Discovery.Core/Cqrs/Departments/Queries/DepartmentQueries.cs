using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;

namespace Discovery.Core.Cqrs.Departments.Queries;

public sealed record ListDepartmentsQuery(Guid? ClientId, bool IncludeGlobal = true)
    : IQuery<Result<IReadOnlyList<DepartmentDto>>>;

public sealed record GetDepartmentByIdQuery(Guid Id)
    : IQuery<Result<DepartmentDto>>;
