using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Repositório para links entre tickets e artigos da base de conhecimento.
/// </summary>
public interface ITicketKnowledgeLinkRepository
{
    /// <summary>Lista links de conhecimento de um ticket.</summary>
    Task<List<TicketKnowledgeLink>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Cria um link entre ticket e artigo KB.</summary>
    Task<TicketKnowledgeLink> CreateAsync(TicketKnowledgeLink link, CancellationToken ct = default);

    /// <summary>Remove um link.</summary>
    Task DeleteAsync(Guid linkId, CancellationToken ct = default);

    /// <summary>Registra feedback em um link (útil/não útil).</summary>
    Task SetFeedbackAsync(Guid linkId, bool useful, CancellationToken ct = default);

    /// <summary>Busca link por ticket + artigo (para evitar duplicatas).</summary>
    Task<TicketKnowledgeLink?> GetByTicketAndArticleAsync(Guid ticketId, Guid articleId, CancellationToken ct = default);
}
