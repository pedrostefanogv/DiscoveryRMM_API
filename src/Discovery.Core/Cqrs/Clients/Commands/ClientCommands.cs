using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Clients.Commands;

public sealed record CreateClientCommand(string Name, string? Notes) : ICommand<Result<Client>>;
public sealed record UpdateClientCommand(Guid Id, string Name, string? Notes, bool IsActive) : ICommand<Result<Client>>;
public sealed record DeleteClientCommand(Guid Id) : ICommand<Result<VoidResult>>;
public sealed record UpsertClientCustomFieldCommand(Guid ClientId, Guid DefinitionId, string ValueJson, string Username) : ICommand<Result<object>>;
