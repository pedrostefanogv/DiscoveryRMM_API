namespace Discovery.Core.Entities;

/// <summary>
/// Item da fila de triagem por IA. Uma linha por chamado; garante que a triagem
/// roda fora do request de criação e que reprocessamentos são idempotentes.
/// </summary>
public class AiAssignmentQueueItem
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime AvailableAt { get; set; }
    public string? LastError { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
