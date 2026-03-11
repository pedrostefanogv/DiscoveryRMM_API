namespace Meduza.Core.Entities;

/// <summary>
/// Vínculo explícito entre um ticket e um artigo da base de conhecimento.
/// </summary>
public class TicketKnowledgeLink
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid ArticleId { get; set; }
    public string? LinkedBy { get; set; }   // usuário ou "ai-suggestion"
    public string? Note { get; set; }       // opcional: por que esse artigo foi linkado
    public DateTime LinkedAt { get; set; }

    public Ticket Ticket { get; set; } = null!;
    public KnowledgeArticle Article { get; set; } = null!;
}
