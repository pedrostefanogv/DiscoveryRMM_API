namespace Discovery.Core.Enums;

public enum SoftwareInventoryOrderBy
{
    SoftwareName = 0,
    Publisher = 1,
    Version = 2,
    LastSeenAt = 3,
    AgentHostname = 4,
    SiteName = 5
}

public enum LogsOrderBy
{
    Timestamp = 0,
    Level = 1,
    Source = 2,
    Type = 3
}

public enum ConfigurationAuditOrderBy
{
    Timestamp = 0,
    EntityType = 1,
    ChangedBy = 2,
    FieldName = 3
}

public enum TicketsOrderBy
{
    Timestamp = 0,
    Priority = 1,
    SlaBreached = 2,
    ClosedAt = 3
}

public enum AgentHardwareOrderBy
{
    SiteName = 0,
    AgentHostname = 1,
    CollectedAt = 2,
    OsName = 3
}

public enum AgentInventoryCompositeOrderBy
{
    SiteName = 0,
    AgentHostname = 1,
    SoftwareName = 2,
    CollectedAt = 3
}
