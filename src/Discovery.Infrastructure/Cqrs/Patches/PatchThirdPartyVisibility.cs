using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>
/// Visibilidade de apps de TERCEIROS (choco/winget) nas visões de patches —
/// a decisão D22 (Origens de patch) aplicada à LEITURA.
///
/// Uma linha choco/winget só é visível quando a política EFETIVA do escopo do
/// agente (Site &gt; Client &gt; Global) inclui "third_party" em
/// <c>patch_policies.sources</c>. Sem política, ou sem "third_party" nela, a
/// origem está DESATIVADA (padrão do produto): os pacotes não aparecem em
/// inventário, pendências, snapshot e conformidade, não contam nos scores e o
/// agente nem os coleta (flag de configuração espelhando esta mesma regra).
///
/// POR QUE CONJUNTOS E NÃO UM JOIN: a tabela de políticas tem UMA linha por
/// escopo (Global/Cliente/Site) — meia dúzia de linhas. Carregá-las em memória
/// e traduzir a resolução como <c>Contains</c> sobre esses conjuntos mantém a
/// avaliação em SQL (necessária para paginação/agrupamento) sem um join lateral
/// por agente. A política efetiva do agente resolve EXATAMENTE como o
/// <see cref="Services.PatchApprovalResolver"/>: Site vence Client, que vence
/// Global; "vence" aqui significa "a política do escopo decide, mesmo que
/// proíba".
/// </summary>
public sealed class PatchThirdPartyVisibility
{
    public bool GlobalAllowsThirdParty { get; private init; }
    /// <summary>Sites que têm política de ESCOPO SITE (decidem, permitindo ou não).</summary>
    public IReadOnlySet<Guid> SitePolicySites { get; private init; } = new HashSet<Guid>();
    /// <summary>Sites cuja política de site PERMITE terceiros.</summary>
    public IReadOnlySet<Guid> SiteAllows { get; private init; } = new HashSet<Guid>();
    /// <summary>Clients que têm política de ESCOPO CLIENT (decidem na ausência de política de site).</summary>
    public IReadOnlySet<Guid> ClientPolicyClients { get; private init; } = new HashSet<Guid>();
    /// <summary>Clients cuja política de client PERMITE terceiros.</summary>
    public IReadOnlySet<Guid> ClientAllows { get; private init; } = new HashSet<Guid>();

    public PatchThirdPartyVisibility(
        bool globalAllowsThirdParty,
        IReadOnlySet<Guid> sitePolicySites,
        IReadOnlySet<Guid> siteAllows,
        IReadOnlySet<Guid> clientPolicyClients,
        IReadOnlySet<Guid> clientAllows)
    {
        GlobalAllowsThirdParty = globalAllowsThirdParty;
        SitePolicySites = sitePolicySites;
        SiteAllows = siteAllows;
        ClientPolicyClients = clientPolicyClients;
        ClientAllows = clientAllows;
    }

    /// <summary>Instância de fábrica com a origem de terceiros DESATIVADA (default).</summary>
    public static PatchThirdPartyVisibility ThirdPartyDisabled { get; } = new(
        false, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    /// <summary>
    /// Carrega TODAS as políticas de patches (tabela pequena, uma linha por
    /// escopo) e pré-computa os conjuntos de resolução. Chamado por consulta —
    /// a leitura enxuta de 5 colunas custa menos que qualquer join de frota.
    /// </summary>
    public static async Task<PatchThirdPartyVisibility> LoadAsync(DiscoveryDbContext db, CancellationToken ct = default)
    {
        List<PatchPolicy> policies;
        try
        {
            policies = await db.PatchPolicies
                .AsNoTracking()
                .ToListAsync(ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not included in the model", StringComparison.OrdinalIgnoreCase))
        {
            // Contexto reduzido (testes InMemory) sem patch_policies no modelo:
            // comporta-se como "nenhuma política" — origem de terceiros
            // DESATIVADA (o default do produto). Em produção o DbSet sempre
            // existe no modelo, então este caminho não é alcançado.
            return ThirdPartyDisabled;
        }

        var sitePolicySites = new HashSet<Guid>();
        var siteAllows = new HashSet<Guid>();
        var clientPolicyClients = new HashSet<Guid>();
        var clientAllows = new HashSet<Guid>();
        var globalAllows = false;

        foreach (var policy in policies)
        {
            var allows = PatchSourcePolicy.AllowsThirdParty(policy.Sources);
            switch (policy.ScopeType)
            {
                case PatchApprovalScopeType.Site when policy.SiteId.HasValue:
                    sitePolicySites.Add(policy.SiteId.Value);
                    if (allows)
                        siteAllows.Add(policy.SiteId.Value);
                    break;
                case PatchApprovalScopeType.Client when policy.ClientId.HasValue:
                    clientPolicyClients.Add(policy.ClientId.Value);
                    if (allows)
                        clientAllows.Add(policy.ClientId.Value);
                    break;
                case PatchApprovalScopeType.Global:
                    globalAllows = allows;
                    break;
            }
        }

        return new PatchThirdPartyVisibility(
            globalAllows, sitePolicySites, siteAllows, clientPolicyClients, clientAllows);
    }

    /// <summary>
    /// Política efetiva permite terceiros para o par site/client? Mesma cadeia
    /// do resolver (Site &gt; Client &gt; Global): a política MAIS ESPECÍFICA
    /// existente decide — incluindo a decisão de PROIBIR.
    /// </summary>
    public bool AllowsThirdParty(Guid siteId, Guid? clientId)
    {
        if (SitePolicySites.Contains(siteId))
            return SiteAllows.Contains(siteId);
        if (clientId.HasValue && ClientPolicyClients.Contains(clientId.Value))
            return ClientAllows.Contains(clientId.Value);
        return GlobalAllowsThirdParty;
    }

    /// <summary>
    /// Política EFETIVA (Site &gt; Client &gt; Global) de UM agente. Usada pelos
    /// caminhos POR AGENTE (aba de atualizações, contadores do snapshot,
    /// resolução de aprovação) — o mesmo veredito que
    /// <see cref="Services.PatchApprovalResolver"/> usa para auto-aprovação.
    /// </summary>
    public static async Task<PatchPolicy?> GetEffectivePolicyForAgentAsync(
        DiscoveryDbContext db,
        Guid agentId,
        CancellationToken ct = default)
    {
        var siteInfo = await db.Agents
            .AsNoTracking()
            .Where(agent => agent.Id == agentId)
            .Join(db.Sites.AsNoTracking(),
                agent => agent.SiteId,
                site => site.Id,
                (agent, site) => new { site.Id, site.ClientId })
            .FirstOrDefaultAsync(ct);
        if (siteInfo is null)
            return null;

        var policies = await db.PatchPolicies
            .AsNoTracking()
            .Where(policy =>
                policy.ScopeType == PatchApprovalScopeType.Global ||
                (policy.ScopeType == PatchApprovalScopeType.Client && policy.ClientId == siteInfo.ClientId) ||
                (policy.ScopeType == PatchApprovalScopeType.Site && policy.SiteId == siteInfo.Id))
            .ToListAsync(ct);

        return policies
            .OrderByDescending(policy => (int)policy.ScopeType)
            .FirstOrDefault();
    }
}
