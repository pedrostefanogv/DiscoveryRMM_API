using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.Configurations.Queries;

public sealed record GetServerConfigQuery : IQuery<Result<ServerConfiguration>>;
public sealed record GetClientConfigQuery(Guid ClientId) : IQuery<Result<ClientConfiguration>>;
public sealed record GetSiteConfigQuery(Guid SiteId) : IQuery<Result<SiteConfiguration>>;
public sealed record GetServerReportingQuery : IQuery<Result<object>>;
public sealed record GetAiCredentialsQuery : IQuery<Result<IReadOnlyList<AiProviderCredential>>>;
public sealed record GetAiModelsQuery(Guid? ClientId, Guid? SiteId, string? Search) : IQuery<Result<object>>;
