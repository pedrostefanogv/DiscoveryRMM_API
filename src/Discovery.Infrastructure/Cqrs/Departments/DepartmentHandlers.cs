using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Departments;

public sealed class CreateDepartmentCommandHandler(
    IDepartmentService service
) : IRequestHandler<CreateDepartmentCommand, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(CreateDepartmentCommand cmd, CancellationToken ct)
    {
        var dept = new Department
        {
            Name = cmd.Name,
            Description = cmd.Description,
            ClientId = cmd.ClientId,
            InheritFromGlobalId = cmd.InheritFromGlobalId,
            SortOrder = cmd.SortOrder,
            IsActive = true
        };
        var created = await service.CreateAsync(dept, ct);
        return Result<DepartmentDto>.Success(Map(created));
    }

    internal static DepartmentDto Map(Department d) => new(
        d.Id, d.ClientId, d.Name, d.Description, d.InheritFromGlobalId,
        d.SortOrder, d.IsActive, d.CreatedAt, d.UpdatedAt);
}

public sealed class UpdateDepartmentCommandHandler(
    IDepartmentService service
) : IRequestHandler<UpdateDepartmentCommand, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(UpdateDepartmentCommand cmd, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(cmd.Id, ct);
        if (dept is null)
            return Result<DepartmentDto>.Failure(Error.NotFound($"Department {cmd.Id} not found"));

        if (cmd.Name is not null) dept.Name = cmd.Name;
        if (cmd.Description is not null) dept.Description = cmd.Description;
        if (cmd.InheritFromGlobalId is not null) dept.InheritFromGlobalId = cmd.InheritFromGlobalId;
        if (cmd.SortOrder.HasValue) dept.SortOrder = cmd.SortOrder.Value;
        if (cmd.IsActive.HasValue) dept.IsActive = cmd.IsActive.Value;

        var updated = await service.UpdateAsync(dept, ct);
        return Result<DepartmentDto>.Success(CreateDepartmentCommandHandler.Map(updated));
    }
}

public sealed class DeleteDepartmentCommandHandler(
    IDepartmentService service
) : IRequestHandler<DeleteDepartmentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteDepartmentCommand cmd, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(cmd.Id, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Department {cmd.Id} not found"));
    }
}

public sealed class ListDepartmentsQueryHandler(
    IDepartmentService service
) : IRequestHandler<ListDepartmentsQuery, Result<IReadOnlyList<DepartmentDto>>>
{
    public async Task<Result<IReadOnlyList<DepartmentDto>>> Handle(ListDepartmentsQuery q, CancellationToken ct)
    {
        var deps = q.ClientId.HasValue
            ? await service.GetByClientAsync(q.ClientId.Value, q.IncludeGlobal, ct)
            : await service.GetGlobalAsync(ct);
        return Result<IReadOnlyList<DepartmentDto>>.Success(
            deps.Select(CreateDepartmentCommandHandler.Map).ToList().AsReadOnly());
    }
}

public sealed class GetDepartmentByIdQueryHandler(
    IDepartmentService service
) : IRequestHandler<GetDepartmentByIdQuery, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(GetDepartmentByIdQuery q, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(q.Id, ct);
        return dept is null
            ? Result<DepartmentDto>.Failure(Error.NotFound($"Department {q.Id} not found"))
            : Result<DepartmentDto>.Success(CreateDepartmentCommandHandler.Map(dept));
    }
}
