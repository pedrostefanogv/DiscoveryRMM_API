using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Tickets.QueryHandlers;

public sealed class GetTicketRelationsQueryHandler(
    ITicketRelationRepository relationRepo,
    DiscoveryDbContext db)
    : IRequestHandler<GetTicketRelationsQuery, Result<IReadOnlyList<TicketRelationDto>>>
{
    public async Task<Result<IReadOnlyList<TicketRelationDto>>> Handle(GetTicketRelationsQuery q, CancellationToken ct)
    {
        var items = await relationRepo.GetByTicketAsync(q.TicketId, ct);

        // Resolve o "outro" chamado de cada vínculo (título/status) em UMA consulta,
        // para o console não exibir apenas o GUID.
        var otherIds = items
            .Select(r => r.SourceTicketId == q.TicketId ? r.TargetTicketId : r.SourceTicketId)
            .Distinct()
            .ToList();

        var others = otherIds.Count == 0
            ? new Dictionary<Guid, (string Title, DateTime? ClosedAt)>()
            : await db.Tickets
                .AsNoTracking()
                .Where(t => otherIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Title, t.ClosedAt })
                .ToDictionaryAsync(t => t.Id, t => new ValueTuple<string, DateTime?>(t.Title, t.ClosedAt), ct);

        var dtos = items
            .Select(r =>
            {
                var otherId = r.SourceTicketId == q.TicketId ? r.TargetTicketId : r.SourceTicketId;
                var hasOther = others.TryGetValue(otherId, out var other);

                return new TicketRelationDto(
                    r.Id,
                    r.SourceTicketId,
                    r.TargetTicketId,
                    ((TicketRelationType)r.RelationTypeValue).ToString(),
                    r.CreatedBy,
                    r.CreatedAt,
                    r.SourceTicketId == q.TicketId ? "source" : "target",
                    hasOther ? otherId : null,
                    hasOther ? other.Item1 : null,
                    hasOther ? other.Item2.HasValue : null);
            })
            .ToList();

        return Result<IReadOnlyList<TicketRelationDto>>.Success(dtos);
    }
}
