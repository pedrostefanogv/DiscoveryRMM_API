namespace Discovery.Core.Enums;

public enum AgentLabelField
{
    Hostname = 0,
    DisplayName = 1,
    IpAddress = 2,
    OperatingSystem = 3,
    OsVersion = 4,
    Status = 5,
    SoftwareName = 6,
    SoftwarePublisher = 7,
    SoftwareVersion = 8,
    SoftwareCount = 9,
    Processor = 10,
    TotalMemoryBytes = 11,
    TotalDisksCount = 12,
    ProcessorCores = 13,
    ProcessorThreads = 14,
    GpuModel = 15,
    GpuMemoryBytes = 16,

    // Disk fields — valid only inside a DiskGroup node
    DiskDriveLetter = 17,
    DiskFreeSpaceBytes = 18,
    DiskTotalSpaceBytes = 19,
    DiskFreeSpacePercent = 20,
    DiskFileSystem = 21,
    DiskMediaType = 22,

    // Custom field references — DefinitionId must be set on the expression node
    AgentCustomField = 100,
    ClientCustomField = 101,
    SiteCustomField = 102
}
