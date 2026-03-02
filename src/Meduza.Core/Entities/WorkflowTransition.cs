namespace Meduza.Core.Entities;

/// <summary>
/// Transição permitida entre estados no workflow de tickets.
/// Define quais mudanças de estado são válidas.
/// </summary>
public class WorkflowTransition
{
    public Guid Id { get; set; }
    public Guid? ClientId { get; set; }
    public Guid FromStateId { get; set; }
    public Guid ToStateId { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
}
