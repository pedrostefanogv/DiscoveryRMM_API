using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Support.Templates;

/// <summary>
/// Lista templates ativos e não excluídos por padrão. Parâmetros de amplo
/// escopo (includeInactive/includeDeleted/allClients) são para a página de
/// administração de templates; consumidores funcionais (abertura de chamado,
/// agent/chat) continuam recebendo apenas os ativos do seu escopo.
/// </summary>
public sealed record ListTicketTemplatesQuery(
    Guid? ClientId = null,
    Guid? DepartmentId = null,
    bool IncludeGlobal = true,
    bool IncludeInactive = false,
    bool IncludeDeleted = false,
    bool AllClients = false)
    : IQuery<Result<IReadOnlyList<TicketTemplateDto>>>;
