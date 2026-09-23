using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Macros;

public sealed record ListTicketMacrosQuery(Guid? ClientId = null, Guid? DepartmentId = null, bool IncludeGlobal = true)
    : IQuery<Result<IReadOnlyList<TicketMacroDto>>>;
