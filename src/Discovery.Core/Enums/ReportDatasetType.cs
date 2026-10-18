namespace Discovery.Core.Enums;

public enum ReportDatasetType
{
    SoftwareInventory = 0,
    Logs = 1,
    ConfigurationAudit = 2,
    Tickets = 3,
    AgentHardware = 4,
    AgentInventoryComposite = 5,
    AgentLabels = 6,
    AutomaticLabelRules = 7,
    AutomationExecutions = 8,
    AgentMonitoringEvents = 9,
    AgentAlerts = 10,
    P2pTelemetry = 11,
    AgentDisks = 12,
    NetworkAdapters = 13,
    ListeningPorts = 14,
    Printers = 15,
    SoftwareCatalog = 16,
    AutomationScripts = 17,
    AppPackages = 18,
    TicketActivity = 19,
    TicketEscalations = 20,
    CustomFields = 21,
    KnowledgeBase = 22,
    TicketAnswers = 23,
    // Gerenciador de Patches do Windows (WUA) e fontes correlatas.
    WindowsUpdates = 24,
    PatchCompliance = 25,
    PatchApprovals = 26,
    PatchApprovalAudit = 27,
    // Inventário/estado dos agentes (fonte que faltava para relatórios de frota).
    Agents = 28
}
