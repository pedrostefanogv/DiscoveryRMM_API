namespace Discovery.Core.Enums;

/// <summary>
/// Estado do ciclo de vida de uma definição de alerta PSADT.
/// </summary>
public enum AlertDefinitionStatus
{
    /// <summary>Criado mas ainda não agendado/enviado (rascunho).</summary>
    Draft = 0,

    /// <summary>Agendado para envio futuro (ScheduledAt está definido).</summary>
    Scheduled = 1,

    /// <summary>Em processo de despacho para os agents.</summary>
    Dispatching = 2,

    /// <summary>Despachado com sucesso para todos os agents do escopo.</summary>
    Dispatched = 3,

    /// <summary>Expirou antes de ser despachado (ExpiresAt ultrapassado).</summary>
    Expired = 4,

    /// <summary>Cancelado manualmente pelo operador.</summary>
    Cancelled = 5
}
