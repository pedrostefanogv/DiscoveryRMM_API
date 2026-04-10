namespace Discovery.Core.Entities;

public class AgentHardwareComponents
{
    public List<DiskInfo> Disks { get; set; } = [];
    public List<NetworkAdapterInfo> NetworkAdapters { get; set; } = [];
    public List<MemoryModuleInfo> MemoryModules { get; set; } = [];
    public List<PrinterInfo> Printers { get; set; } = [];
}