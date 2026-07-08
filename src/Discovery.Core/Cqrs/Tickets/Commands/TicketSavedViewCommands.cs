using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Tickets.Commands;

public sealed record CreateTicketSavedViewCommand(string Name, string? FilterJson, bool IsShared, Guid? UserId) : ICommand<Result<TicketSavedView>>;
public sealed record UpdateTicketSavedViewCommand(Guid Id, string? Name, string? FilterJson, bool? IsShared) : ICommand<Result<TicketSavedView>>;
public sealed record DeleteTicketSavedViewCommand(Guid Id) : ICommand<Result<VoidResult>>;
