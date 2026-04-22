namespace Discovery.Core.Entities;

/// <summary>
/// Nota vinculada a uma entidade do sistema (Client, Site ou Agent).
/// Exatamente um dos campos ClientId/SiteId/AgentId deve ser preenchido.
/// </summary>
public class EntityNote
{
    public Guid Id { get; set; }

    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    public string Content { get; set; } = string.Empty;
    public string? Author { get; set; }
    public bool IsPinned { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}