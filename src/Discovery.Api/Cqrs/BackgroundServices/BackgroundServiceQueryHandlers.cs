using Discovery.Api.Services.BackgroundServices;
using Discovery.Core.Cqrs;
using MediatR;

namespace Discovery.Api.Cqrs.BackgroundServices;

public sealed class ListBackgroundServicesQueryHandler(BackgroundServiceRegistry registry)
    : IRequestHandler<ListBackgroundServicesQuery, Result<IReadOnlyList<BackgroundServiceSnapshot>>>
{
    public Task<Result<IReadOnlyList<BackgroundServiceSnapshot>>> Handle(ListBackgroundServicesQuery q, CancellationToken ct)
    {
        var snapshot = registry.Snapshot();
        return Task.FromResult(Result<IReadOnlyList<BackgroundServiceSnapshot>>.Success(snapshot));
    }
}

public sealed class GetBackgroundServiceByNameQueryHandler(BackgroundServiceRegistry registry)
    : IRequestHandler<GetBackgroundServiceByNameQuery, Result<BackgroundServiceSnapshot>>
{
    public Task<Result<BackgroundServiceSnapshot>> Handle(GetBackgroundServiceByNameQuery q, CancellationToken ct)
    {
        var snapshot = registry.Get(q.Name);
        if (snapshot is null)
            return Task.FromResult(Result<BackgroundServiceSnapshot>.Failure(Error.NotFound($"Background service '{q.Name}' not found")));
        return Task.FromResult(Result<BackgroundServiceSnapshot>.Success(snapshot));
    }
}
