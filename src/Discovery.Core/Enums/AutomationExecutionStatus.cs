namespace Discovery.Core.Enums;

public enum AutomationExecutionStatus
{
    Dispatched = 0,
    Acknowledged = 1,
    Completed = 2,
    Failed = 3,
    /// <summary>
    /// Cancelada pelo operador antes de finalizar. Também marca o comando como
    /// terminal, o que o retira da reentrega automática.
    /// </summary>
    Cancelled = 4
}
