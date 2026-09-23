namespace Discovery.Core.Enums;

public enum NotificationChannelType
{
    Webhook = 0,
    Email = 1
}

/// <summary>Estratégia de auto-atribuição de chamado por departamento.</summary>
public enum TicketAssignmentStrategy
{
    None = 0,
    RoundRobin = 1,
    LeastOpenTickets = 2
}
