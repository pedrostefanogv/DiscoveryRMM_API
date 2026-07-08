using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFields.Commands;

namespace Discovery.Core.Cqrs.CustomFields.Queries;

public sealed record ListCustomFieldsQuery(string? ScopeType, bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<CustomFieldDto>>>;
public sealed record GetCustomFieldByIdQuery(Guid Id) : IQuery<Result<CustomFieldDto>>;