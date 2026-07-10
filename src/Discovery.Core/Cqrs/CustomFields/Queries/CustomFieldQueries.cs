using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.CustomFields.Queries;

public sealed record ListCustomFieldsQuery(string? ScopeType, bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<CustomFieldDto>>>;
public sealed record GetCustomFieldByIdQuery(Guid Id) : IQuery<Result<CustomFieldDto>>;

public sealed record ListCustomFieldValuesQuery(
    string ScopeType,
    Guid? EntityId = null,
    string? Cursor = null,
    int Limit = 50,
    bool IncludeSecrets = true
) : IQuery<Result<CursorPageDto<CustomFieldResolvedValueDto>>>;