using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.UserGroups.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Identity;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.UserGroups;

public sealed class CreateUserGroupCommandHandler(
    IUserGroupService service
) : IRequestHandler<CreateUserGroupCommand, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(CreateUserGroupCommand cmd, CancellationToken ct)
    {
        var group = new UserGroup
        {
            Name = cmd.Name,
            Description = cmd.Description,
            IsActive = true
        };
        var created = await service.CreateAsync(group, ct);
        return Result<UserGroupDto>.Success(Map(created));
    }

    internal static UserGroupDto Map(UserGroup g) => new(
        g.Id, g.Name, g.Description, g.IsActive, g.CreatedAt, g.UpdatedAt);
}

public sealed class UpdateUserGroupCommandHandler(
    IUserGroupService service
) : IRequestHandler<UpdateUserGroupCommand, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(UpdateUserGroupCommand cmd, CancellationToken ct)
    {
        var group = await service.GetByIdAsync(cmd.Id, ct);
        if (group is null)
            return Result<UserGroupDto>.Failure(Error.NotFound($"UserGroup {cmd.Id} not found"));

        if (cmd.Name is not null) group.Name = cmd.Name;
        if (cmd.Description is not null) group.Description = cmd.Description;
        if (cmd.IsActive.HasValue) group.IsActive = cmd.IsActive.Value;

        var updated = await service.UpdateAsync(group, ct);
        return Result<UserGroupDto>.Success(CreateUserGroupCommandHandler.Map(updated));
    }
}

public sealed class DeleteUserGroupCommandHandler(
    IUserGroupService service
) : IRequestHandler<DeleteUserGroupCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteUserGroupCommand cmd, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(cmd.Id, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"UserGroup {cmd.Id} not found"));
    }
}

public sealed class ListUserGroupsQueryHandler(
    IUserGroupService service
) : IRequestHandler<ListUserGroupsQuery, Result<IReadOnlyList<UserGroupDto>>>
{
    public async Task<Result<IReadOnlyList<UserGroupDto>>> Handle(ListUserGroupsQuery q, CancellationToken ct)
    {
        var groups = await service.GetAllAsync(ct);
        return Result<IReadOnlyList<UserGroupDto>>.Success(
            groups.Select(CreateUserGroupCommandHandler.Map).ToList().AsReadOnly());
    }
}

public sealed class GetUserGroupByIdQueryHandler(
    IUserGroupService service
) : IRequestHandler<GetUserGroupByIdQuery, Result<UserGroupDto>>
{
    public async Task<Result<UserGroupDto>> Handle(GetUserGroupByIdQuery q, CancellationToken ct)
    {
        var group = await service.GetByIdAsync(q.Id, ct);
        return group is null
            ? Result<UserGroupDto>.Failure(Error.NotFound($"UserGroup {q.Id} not found"))
            : Result<UserGroupDto>.Success(CreateUserGroupCommandHandler.Map(group));
    }
}
