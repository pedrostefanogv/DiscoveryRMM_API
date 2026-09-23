using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Departments;

public sealed record AddDepartmentMemberCommand(Guid DepartmentId, Guid UserId) : ICommand<Result<DepartmentMemberDto>>;
public sealed record RemoveDepartmentMemberCommand(Guid DepartmentId, Guid UserId) : ICommand<Result<VoidResult>>;
