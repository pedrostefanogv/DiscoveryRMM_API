using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.CustomFields.Queries;

public sealed record ListCustomFieldsQuery(CustomFieldScopeType? ScopeType, bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<CustomFieldDto>>>;
public sealed record GetCustomFieldByIdQuery(Guid Id) : IQuery<Result<CustomFieldDto>>;

public sealed record ListCustomFieldValuesQuery(
    CustomFieldScopeType ScopeType,
    Guid? EntityId = null,
    string? Cursor = null,
    int Limit = 50,
    bool IncludeSecrets = true
) : IQuery<Result<CursorPageDto<CustomFieldResolvedValueDto>>>;