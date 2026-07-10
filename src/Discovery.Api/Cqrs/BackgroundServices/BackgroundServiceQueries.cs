using Discovery.Api.Services.BackgroundServices;
using Discovery.Core.Cqrs;

namespace Discovery.Api.Cqrs.BackgroundServices;

public sealed record ListBackgroundServicesQuery : IQuery<Result<IReadOnlyList<BackgroundServiceSnapshot>>>;
public sealed record GetBackgroundServiceByNameQuery(string Name) : IQuery<Result<BackgroundServiceSnapshot>>;
