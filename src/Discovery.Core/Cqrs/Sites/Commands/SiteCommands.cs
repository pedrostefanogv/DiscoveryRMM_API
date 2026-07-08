using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Sites.Commands;

public sealed record CreateSiteCommand(Guid ClientId, string Name, string? Notes) : ICommand<Result<Site>>;
public sealed record UpdateSiteCommand(Guid ClientId, Guid SiteId, string Name, string? Notes, bool IsActive) : ICommand<Result<Site>>;
public sealed record DeleteSiteCommand(Guid ClientId, Guid SiteId) : ICommand<Result<VoidResult>>;
public sealed record UpsertSiteCustomFieldCommand(Guid ClientId, Guid SiteId, Guid DefinitionId, string ValueJson, string Username) : ICommand<Result<object>>;
