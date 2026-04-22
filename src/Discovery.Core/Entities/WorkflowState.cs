namespace Discovery.Core.Entities;

/// <summary>
/// Estado customizável de workflow para tickets.
/// Cada client pode ter seus próprios estados; estados com client_id NULL são globais (default).
/// </summary>
public class WorkflowState
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public bool IsInitial { get; set; }
    public bool IsFinal { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Quando true, o SLA é pausado enquanto o ticket estiver neste estado (ex: Aguardando cliente).</summary>
    public bool PausesSla { get; set; } = false;
    public DateTime CreatedAt { get; set; }
}
