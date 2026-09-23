using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Templates;

public sealed record ListTicketTemplatesQuery(Guid? ClientId = null, Guid? DepartmentId = null, bool IncludeGlobal = true)
    : IQuery<Result<IReadOnlyList<TicketTemplateDto>>>;
