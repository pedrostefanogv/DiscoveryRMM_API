using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>
/// Ponto único de resolução do site usado como escopo das configurações de IA.
///
/// Chamados criados pelo agent, por alertas ou por regras automáticas podem não
/// ter SiteId, e a resolução de configuração (ResolveForSiteAsync) exige um site
/// existente para aplicar a herança Site -> Cliente -> Servidor. Nesses casos
/// usamos um site do mesmo cliente.
/// </summary>
public static class AiSiteScopeResolver
{
    public static async Task<Guid?> ResolveAsync(
        DiscoveryDbContext db, Guid? siteId, Guid clientId, CancellationToken ct = default)
    {
        if (siteId.HasValue && siteId.Value != Guid.Empty) return siteId;

        return await db.Sites.AsNoTracking()
            .Where(s => s.ClientId == clientId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }
}
