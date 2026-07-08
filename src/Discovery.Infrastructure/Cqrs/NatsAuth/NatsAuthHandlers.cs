using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.NatsAuth.Queries;
using MediatR;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Cqrs.NatsAuth;

public sealed class GetNatsStatusQueryHandler : IRequestHandler<GetNatsStatusQuery, Result<NatsStatusDto>>
{
    private readonly NatsConnection _natsConnection;

    public GetNatsStatusQueryHandler(NatsConnection natsConnection)
    {
        _natsConnection = natsConnection;
    }

    public Task<Result<NatsStatusDto>> Handle(GetNatsStatusQuery q, CancellationToken ct)
    {
        var connected = _natsConnection.ConnectionState == NatsConnectionState.Open;
        var serverUrl = _natsConnection.Opts.Url;
        var lastPing = connected ? (DateTime?)DateTime.UtcNow : null;

        return Task.FromResult(Result<NatsStatusDto>.Success(new NatsStatusDto(connected, serverUrl, lastPing)));
    }
}
