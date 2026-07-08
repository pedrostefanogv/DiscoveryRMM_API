using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Clients.Queries;

public sealed record GetAllClientsQuery(bool IncludeInactive = false) : IQuery<Result<IReadOnlyList<Client>>>;
public sealed record GetClientByIdQuery(Guid Id) : IQuery<Result<Client>>;
public sealed record GetClientCustomFieldsQuery(Guid ClientId, bool IncludeSecrets = true) : IQuery<Result<IReadOnlyList<object>>>;
