namespace Discovery.Core.Entities;

public class AgentHardwareComponents
{
    public List<DiskInfo> Disks { get; set; } = [];
    public List<NetworkAdapterInfo> NetworkAdapters { get; set; } = [];
    public List<MemoryModuleInfo> MemoryModules { get; set; } = [];
    public List<PrinterInfo> Printers { get; set; } = [];
    public List<ListeningPortInfo> ListeningPorts { get; set; } = [];
    public List<OpenSocketInfo> OpenSockets { get; set; } = [];
    // Itens de inicialização e tarefas agendadas (JSON no HardwareComponentsJson —
    // sem coluna própria; agents antigos simplesmente não enviam as listas).
    public List<StartupItemInfo> StartupItems { get; set; } = [];
    public List<ScheduledTaskInfo> ScheduledTasks { get; set; } = [];

    // ── Windows Server ──────────────────────────────────────────────────
    // Coletados SÓ quando a máquina é Windows Server (o agente faz gate por
    // IsServer). Em estações de trabalho e agentes antigos ficam vazios — por
    // isso a checagem de "inventário vazio" em TryBuildFromInventoryRaw
    // PRECISA considerá-los, senão um servidor que só reportasse dados de
    // servidor seria descartado.
    public List<AgentWindowsRole> WindowsRoles { get; set; } = [];
    public List<AgentRaidController> RaidControllers { get; set; } = [];
    public List<AgentVirtualMachine> VirtualMachines { get; set; } = [];
    public List<AgentCluster> Clusters { get; set; } = [];
    public List<AgentStoragePool> StoragePools { get; set; } = [];
    public List<AgentRdsSession> RdsSessions { get; set; } = [];

    // Estado do host (não são listas): se é RDS Host e se o inventário de
    // servidor foi coletado. Nullable para distinguir "não reportado" (agente
    // antigo / estação) de false.
    /// <summary>true quando o host é RDS Host.</summary>
    public bool? IsRdsHost { get; set; }

    /// <summary>Motivo pelo qual a coleta de dados de servidor falhou/foi omitida.</summary>
    public string? ServerInventoryReason { get; set; }
}