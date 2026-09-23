using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class GetTicketRelationsQueryHandler(ITicketRelationRepository relationRepo)
    : IRequestHandler<GetTicketRelationsQuery, Result<IReadOnlyList<TicketRelationDto>>>
{
    public async Task<Result<IReadOnlyList<TicketRelationDto>>> Handle(GetTicketRelationsQuery q, CancellationToken ct)
    {
        var items = await relationRepo.GetByTicketAsync(q.TicketId, ct);

        var dtos = items
            .Select(r => new TicketRelationDto(
                r.Id,
                r.SourceTicketId,
                r.TargetTicketId,
                ((TicketRelationType)r.RelationTypeValue).ToString(),
                r.CreatedBy,
                r.CreatedAt,
                r.SourceTicketId == q.TicketId ? "source" : "target"))
            .ToList();

        return Result<IReadOnlyList<TicketRelationDto>>.Success(dtos);
    }
}
