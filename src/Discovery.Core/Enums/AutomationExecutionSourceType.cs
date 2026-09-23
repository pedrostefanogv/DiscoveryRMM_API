namespace Discovery.Core.Enums;

public enum AutomationExecutionSourceType
{
    RunNow = 0,
    Scheduled = 1,
    ForceSync = 2,
    AgentManual = 3,
    // Operações de software disparadas pelo detalhe do agente (inventário).
    SoftwareUpdate = 4,
    SoftwareUninstall = 5
}
