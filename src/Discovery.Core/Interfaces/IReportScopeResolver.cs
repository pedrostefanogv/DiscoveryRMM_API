using Discovery.Core.Enums.Identity;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve o escopo efetivo (cliente/site) de um usuario para relatorios,
/// a partir das permissoes reais — nunca do corpo da requisicao.
/// </summary>
public interface IReportScopeResolver
{
    Task<ReportScopeResolution> ResolveAsync(
        ResourceType resource,
        ActionType action,
        Guid? requestedClientId,
        Guid? requestedSiteId,
        CancellationToken cancellationToken = default);
}
