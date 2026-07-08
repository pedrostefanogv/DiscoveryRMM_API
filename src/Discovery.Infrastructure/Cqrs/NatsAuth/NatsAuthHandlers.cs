using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.NatsAuth.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.NatsAuth;

public sealed class GetNatsStatusQueryHandler : IRequestHandler<GetNatsStatusQuery, Result<NatsStatusDto>>
{
    public Task<Result<NatsStatusDto>> Handle(GetNatsStatusQuery q, CancellationToken ct)
    {
        return Task.FromResult(Result<NatsStatusDto>.Success(new NatsStatusDto(false, null, null)));
    }
}
