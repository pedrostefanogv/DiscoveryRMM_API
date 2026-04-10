namespace Discovery.Core.Enums;

/// <summary>
/// Tipos de atividades que podem ser registradas em um ticket
/// </summary>
public enum TicketActivityType
{
    Created = 0,
    StateChanged = 1,
    Assigned = 2,
    Commented = 3,
    SlaWarning = 4,
    SlaBreached = 5,
    Escalated = 6,
    Reopened = 7,
    DepartmentChanged = 8,
    PriorityChanged = 9,
    DescriptionUpdated = 10,
    CategoryChanged = 11,
    Deleted = 12
}
