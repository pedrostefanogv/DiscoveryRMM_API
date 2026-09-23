using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Departments;

public sealed record ListDepartmentMembersQuery(Guid DepartmentId) : IQuery<Result<IReadOnlyList<DepartmentMemberDto>>>;
