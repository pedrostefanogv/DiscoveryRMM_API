using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve o escopo de relatorios com a seguinte politica:
/// - acesso global escolhe livremente o cliente/site (ou nenhum = todos);
/// - acesso por cliente so pode operar dentro dos clientes permitidos;
/// - acesso apenas por site e resolvido para o cliente daquele site;
/// - sem nenhum escopo e negado.
/// </summary>
public class ReportScopeResolver(IScopeContext scopeContext, ISiteRepository sites) : IReportScopeResolver
{
    public async Task<ReportScopeResolution> ResolveAsync(
        ResourceType resource,
        ActionType action,
        Guid? requestedClientId,
        Guid? requestedSiteId,
        CancellationToken cancellationToken = default)
    {
        var access = await scopeContext.GetAccessAsync(resource, action);

        if (access.HasGlobalAccess)
            return new ReportScopeResolution(true, true, requestedClientId, requestedSiteId, [], null);

        if (access.AllowedClientIds.Count > 0)
        {
            if (requestedClientId is { } requested && !access.AllowedClientIds.Contains(requested))
                return Deny("O cliente informado esta fora do escopo do usuario.");

            var clientId = requestedClientId
                ?? (access.AllowedClientIds.Count == 1 ? access.AllowedClientIds[0] : (Guid?)null);

            if (clientId is null)
                return Deny("Informe o cliente: o usuario tem acesso a mais de um.");

            return new ReportScopeResolution(true, false, clientId, requestedSiteId, access.AllowedClientIds, null);
        }

        if (access.AllowedSiteIds.Count > 0)
        {
            Guid siteId;
            if (requestedSiteId is { } requestedSite)
            {
                if (!access.AllowedSiteIds.Contains(requestedSite))
                    return Deny("O site informado esta fora do escopo do usuario.");
                siteId = requestedSite;
            }
            else if (access.AllowedSiteIds.Count == 1)
            {
                siteId = access.AllowedSiteIds[0];
            }
            else
            {
                return Deny("Informe o site: o usuario tem acesso a mais de um.");
            }

            var site = await sites.GetByIdAsync(siteId);
            if (site is null)
                return Deny("Site nao encontrado.");

            return new ReportScopeResolution(true, false, site.ClientId, siteId, [site.ClientId], null);
        }

        return Deny("O usuario nao possui acesso a relatorios.");
    }

    private static ReportScopeResolution Deny(string error)
        => new(false, false, null, null, [], error);
}
