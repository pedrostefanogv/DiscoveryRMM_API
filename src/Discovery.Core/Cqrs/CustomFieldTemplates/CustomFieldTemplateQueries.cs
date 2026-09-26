using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.CustomFieldTemplates;

public sealed record ListCustomFieldTemplatesQuery(
    Guid? ClientId = null,
    Guid? DepartmentId = null,
    bool IncludeGlobal = true,
    bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<CustomFieldTemplateDto>>>;
