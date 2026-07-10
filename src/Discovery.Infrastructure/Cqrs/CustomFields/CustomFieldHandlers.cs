using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.CustomFields;

public sealed class ListCustomFieldsQueryHandler(ICustomFieldService svc) : IRequestHandler<ListCustomFieldsQuery, Result<IReadOnlyList<CustomFieldDto>>>
{
    public async Task<Result<IReadOnlyList<CustomFieldDto>>> Handle(ListCustomFieldsQuery q, CancellationToken ct)
    {
        CustomFieldScopeType? st = q.ScopeType is not null && Enum.TryParse<CustomFieldScopeType>(q.ScopeType, true, out var t) ? t : null;
        var defs = await svc.GetDefinitionsAsync(st, q.IncludeInactive, ct);
        var items = defs.Select(d => new CustomFieldDto(d.Id, d.Name, d.Label, d.Description, d.ScopeType.ToString(), d.DataType.ToString(), d.IsRequired, d.IsSecret, d.IsActive, d.DepartmentId, d.CreatedAt, d.UpdatedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<CustomFieldDto>>.Success(items);
    }
}

public sealed class GetCustomFieldByIdQueryHandler(ICustomFieldService svc) : IRequestHandler<GetCustomFieldByIdQuery, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(GetCustomFieldByIdQuery q, CancellationToken ct)
    {
        var d = await svc.GetDefinitionByIdAsync(q.Id, ct);
        if (d is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {q.Id} not found"));
        return Result<CustomFieldDto>.Success(new CustomFieldDto(d.Id, d.Name, d.Label, d.Description, d.ScopeType.ToString(), d.DataType.ToString(), d.IsRequired, d.IsSecret, d.IsActive, d.DepartmentId, d.CreatedAt, d.UpdatedAt));
    }
}

public sealed class CreateCustomFieldCommandHandler(ICustomFieldService svc) : IRequestHandler<CreateCustomFieldCommand, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(CreateCustomFieldCommand cmd, CancellationToken ct)
    {
        Enum.TryParse<CustomFieldScopeType>(cmd.ScopeType, true, out var st);
        Enum.TryParse<CustomFieldDataType>(cmd.DataType, true, out var dt);
        var input = new UpsertCustomFieldDefinitionInput(cmd.Name, cmd.Label, cmd.Description, st, dt, cmd.IsRequired, true, cmd.IsSecret, null, cmd.ValidationRegex, null, null, null, null, false, false, CustomFieldRuntimeAccessMode.Disabled, null);
        var d = await svc.CreateDefinitionAsync(input, cmd.UpdatedBy, ct);
        if (d is null) return Result<CustomFieldDto>.Failure(Error.Internal("Failed to create custom field"));
        return Result<CustomFieldDto>.Success(new CustomFieldDto(d.Id, d.Name, d.Label, d.Description, d.ScopeType.ToString(), d.DataType.ToString(), d.IsRequired, d.IsSecret, d.IsActive, d.DepartmentId, d.CreatedAt, d.UpdatedAt));
    }
}

public sealed class UpdateCustomFieldCommandHandler(ICustomFieldService svc) : IRequestHandler<UpdateCustomFieldCommand, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(UpdateCustomFieldCommand cmd, CancellationToken ct)
    {
        var d = await svc.GetDefinitionByIdAsync(cmd.Id, ct);
        if (d is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));
        var input = new UpsertCustomFieldDefinitionInput(cmd.Name ?? d.Name, cmd.Label ?? d.Label, cmd.Description ?? d.Description, d.ScopeType, d.DataType, cmd.IsRequired ?? d.IsRequired, cmd.IsActive ?? d.IsActive, cmd.IsSecret ?? d.IsSecret, null, cmd.ValidationRegex, null, null, null, null, false, false, CustomFieldRuntimeAccessMode.Disabled, null);
        var updated = await svc.UpdateDefinitionAsync(cmd.Id, input, cmd.UpdatedBy, ct);
        if (updated is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));
        return Result<CustomFieldDto>.Success(new CustomFieldDto(updated.Id, updated.Name, updated.Label, updated.Description, updated.ScopeType.ToString(), updated.DataType.ToString(), updated.IsRequired, updated.IsSecret, updated.IsActive, updated.DepartmentId, updated.CreatedAt, updated.UpdatedAt));
    }
}

public sealed class DeactivateCustomFieldCommandHandler(ICustomFieldService svc) : IRequestHandler<DeactivateCustomFieldCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeactivateCustomFieldCommand cmd, CancellationToken ct)
    {
        var ok = await svc.DeactivateDefinitionAsync(cmd.Id, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));
    }
}

public sealed class ListCustomFieldValuesQueryHandler(ICustomFieldService svc) : IRequestHandler<ListCustomFieldValuesQuery, Result<CursorPageDto<CustomFieldResolvedValueDto>>>
{
    public async Task<Result<CursorPageDto<CustomFieldResolvedValueDto>>> Handle(ListCustomFieldValuesQuery q, CancellationToken ct)
    {
        if (!Enum.TryParse<CustomFieldScopeType>(q.ScopeType, true, out var scopeType))
            return Result<CursorPageDto<CustomFieldResolvedValueDto>>.Failure(Error.Validation("scopeType", $"Invalid scopeType: {q.ScopeType}"));

        var page = await svc.GetValuesPageAsync(scopeType, q.EntityId, q.Cursor, q.Limit, q.IncludeSecrets, ct);
        return Result<CursorPageDto<CustomFieldResolvedValueDto>>.Success(page);
    }
}

public sealed class UpsertCustomFieldValueCommandHandler(ICustomFieldService svc) : IRequestHandler<UpsertCustomFieldValueCommand, Result<CustomFieldResolvedValueDto>>
{
    public async Task<Result<CustomFieldResolvedValueDto>> Handle(UpsertCustomFieldValueCommand cmd, CancellationToken ct)
    {
        if (!Enum.TryParse<CustomFieldScopeType>(cmd.ScopeType, true, out var scopeType))
            return Result<CustomFieldResolvedValueDto>.Failure(Error.Validation("scopeType", $"Invalid scopeType: {cmd.ScopeType}"));

        var input = new UpsertCustomFieldValueInput(cmd.DefinitionId, scopeType, cmd.EntityId, cmd.ValueJson, cmd.UpdatedBy);
        var result = await svc.UpsertValueAsync(input, ct);
        return Result<CustomFieldResolvedValueDto>.Success(result);
    }
}
