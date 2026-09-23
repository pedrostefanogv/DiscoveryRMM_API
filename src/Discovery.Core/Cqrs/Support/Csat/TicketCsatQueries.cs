using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Csat;

/// <summary>Resumo de CSAT por período, opcionalmente filtrado por cliente/departamento.</summary>
public sealed record GetTicketCsatSummaryQuery(
    DateTime? From = null, DateTime? To = null, Guid? ClientId = null, Guid? DepartmentId = null)
    : IQuery<Result<TicketCsatSummaryDto>>;
