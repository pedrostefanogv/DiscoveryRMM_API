using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Jobs.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Jobs;

public sealed class ListBackgroundJobsQueryHandler : IRequestHandler<ListBackgroundJobsQuery, Result<IReadOnlyList<JobDto>>>
{
    public Task<Result<IReadOnlyList<JobDto>>> Handle(ListBackgroundJobsQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<IReadOnlyList<JobDto>>.Success(Array.Empty<JobDto>()));
    }
}
