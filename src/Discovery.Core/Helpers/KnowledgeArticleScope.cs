using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Visibilidade de artigos da base de conhecimento para o AGENTE.
///
/// Regra (site → client → global): o agente alcança artigos PUBLICADOS sem
/// escopo (globais), do seu cliente ou do seu site. É exatamente a semântica de
/// IKnowledgeArticleRepository.ListByUserScopeAsync quando chamado com
/// filterClientId/filterSiteId pelo handler da listagem do agente — mantida aqui
/// num ponto único para a leitura de imagem não divergir da listagem.
/// </summary>
public static class KnowledgeArticleScope
{
    public static bool IsVisibleToAgent(KnowledgeArticle? article, Guid? agentClientId, Guid agentSiteId)
    {
        if (article is null) return false;
        if (!string.Equals(article.Status, ArticleStatus.Published.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Escopo mais específico manda: artigo de cliente só vale para o cliente
        // do agente; artigo de site só para o site dele.
        if (article.ClientId.HasValue && article.ClientId.Value != agentClientId)
            return false;

        if (article.SiteId.HasValue && article.SiteId.Value != agentSiteId)
            return false;

        return true;
    }
}
