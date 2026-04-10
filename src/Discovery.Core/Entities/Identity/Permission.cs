using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Entities.Identity;

/// <summary>
/// Uma permissão granular: recurso + ação permitida.
/// Ex: ResourceType=Tickets, ActionType=Create.
/// </summary>
public class Permission
{
    public Guid Id { get; set; }
    public ResourceType ResourceType { get; set; }
    public ActionType ActionType { get; set; }
    public string? Description { get; set; }
}
