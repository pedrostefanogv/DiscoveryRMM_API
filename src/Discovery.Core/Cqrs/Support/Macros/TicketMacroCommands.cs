using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Macros;

public sealed record CreateTicketMacroCommand(
    Guid? ClientId, Guid? DepartmentId, string Name, string? Description, string Content,
    bool IsActive, string? CreatedBy) : ICommand<Result<TicketMacroDto>>;

public sealed record UpdateTicketMacroCommand(
    Guid Id, Guid? ClientId, Guid? DepartmentId, string Name, string? Description, string Content,
    bool IsActive) : ICommand<Result<TicketMacroDto>>;

public sealed record DeleteTicketMacroCommand(Guid Id) : ICommand<Result<VoidResult>>;
