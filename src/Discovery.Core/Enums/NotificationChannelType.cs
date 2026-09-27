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
    LeastOpenTickets = 2,

    /// <summary>
    /// Triagem por IA: o responsável é escolhido cruzando o conteúdo/dificuldade do
    /// chamado com métricas, afinidade e competências dos membros do departamento.
    /// </summary>
    AiTriage = 3
}
