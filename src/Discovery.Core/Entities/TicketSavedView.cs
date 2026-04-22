namespace Discovery.Core.Entities;

/// <summary>
/// Visão salva de fila de tickets: armazena um filtro nomeado por usuário.
/// </summary>
public class TicketSavedView
{
    public Guid Id { get; set; }

    /// <summary>Usuário dono desta visão (null = visão global/compartilhada).</summary>
    public Guid? UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>JSON serializado de TicketFilterQuery.</summary>
    public string FilterJson { get; set; } = "{}";

    public bool IsShared { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
