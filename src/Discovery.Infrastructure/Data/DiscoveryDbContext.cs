using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;

namespace Discovery.Infrastructure.Data;

public partial class DiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
    public DbSet<AgentHardwareInfo> AgentHardwareInfos => Set<AgentHardwareInfo>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<ConfigurationAudit> ConfigurationAudits => Set<ConfigurationAudit>();
    public DbSet<ClientConfiguration> ClientConfigurations => Set<ClientConfiguration>();
    public DbSet<DeployToken> DeployTokens => Set<DeployToken>();
    public DbSet<EntityNote> EntityNotes => Set<EntityNote>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<ServerConfiguration> ServerConfigurations => Set<ServerConfiguration>();
    public DbSet<MeshCentralRightsProfile> MeshCentralRightsProfiles => Set<MeshCentralRightsProfile>();
    public DbSet<SiteConfiguration> SiteConfigurations => Set<SiteConfiguration>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<WorkflowState> WorkflowStates => Set<WorkflowState>();
    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();
    public DbSet<SoftwareCatalog> SoftwareCatalogs => Set<SoftwareCatalog>();
    public DbSet<AgentSoftwareInventory> AgentSoftwareInventories => Set<AgentSoftwareInventory>();
    
    // New DbSets for ticket enhancements
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<WorkflowProfile> WorkflowProfiles => Set<WorkflowProfile>();
    public DbSet<TicketActivityLog> TicketActivityLogs => Set<TicketActivityLog>();
    public DbSet<TicketSavedView> TicketSavedViews => Set<TicketSavedView>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ReportTemplateHistory> ReportTemplateHistories => Set<ReportTemplateHistory>();
    public DbSet<ReportExecution> ReportExecutions => Set<ReportExecution>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<AppNotification> AppNotifications => Set<AppNotification>();
    
    // AI Chat & MCP
    public DbSet<AiChatSession> AiChatSessions => Set<AiChatSession>();
    public DbSet<AiChatMessage> AiChatMessages => Set<AiChatMessage>();
    public DbSet<AiChatJob> AiChatJobs => Set<AiChatJob>();
    public DbSet<McpToolPolicy> McpToolPolicies => Set<McpToolPolicy>();
    public DbSet<AppApprovalRule> AppApprovalRules => Set<AppApprovalRule>();
    public DbSet<AppApprovalAudit> AppApprovalAudits => Set<AppApprovalAudit>();
    public DbSet<AutomationScriptDefinition> AutomationScriptDefinitions => Set<AutomationScriptDefinition>();
    public DbSet<AutomationScriptAudit> AutomationScriptAudits => Set<AutomationScriptAudit>();
    public DbSet<AutomationTaskDefinition> AutomationTaskDefinitions => Set<AutomationTaskDefinition>();
    public DbSet<AutomationTaskAudit> AutomationTaskAudits => Set<AutomationTaskAudit>();
    public DbSet<AutomationExecutionReport> AutomationExecutionReports => Set<AutomationExecutionReport>();
    public DbSet<SyncPingDelivery> SyncPingDeliveries => Set<SyncPingDelivery>();
    public DbSet<AgentRelease> AgentReleases => Set<AgentRelease>();
    public DbSet<AgentReleaseArtifact> AgentReleaseArtifacts => Set<AgentReleaseArtifact>();
    public DbSet<AgentUpdateEvent> AgentUpdateEvents => Set<AgentUpdateEvent>();
    public DbSet<AppPackage> AppPackages => Set<AppPackage>();
    public DbSet<ChocolateyPackage> ChocolateyPackages => Set<ChocolateyPackage>();
    public DbSet<WingetPackage> WingetPackages => Set<WingetPackage>();
    public DbSet<AgentLabelRule> AgentLabelRules => Set<AgentLabelRule>();
    public DbSet<AgentLabel> AgentLabels => Set<AgentLabel>();
    public DbSet<AgentLabelRuleMatch> AgentLabelRuleMatches => Set<AgentLabelRuleMatch>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<CustomFieldExecutionAccess> CustomFieldExecutionAccesses => Set<CustomFieldExecutionAccess>();

    // Auto Ticket
    public DbSet<AutoTicketRule> AutoTicketRules => Set<AutoTicketRule>();
    public DbSet<AutoTicketRuleExecution> AutoTicketRuleExecutions => Set<AutoTicketRuleExecution>();
    public DbSet<AlertCorrelationLock> AlertCorrelationLocks => Set<AlertCorrelationLock>();
    public DbSet<AgentMonitoringEvent> AgentMonitoringEvents => Set<AgentMonitoringEvent>();

    // Agent Alerts (PSADT)
    public DbSet<AgentAlertDefinition> AgentAlertDefinitions => Set<AgentAlertDefinition>();
    public DbSet<TicketAlertRule> TicketAlertRules => Set<TicketAlertRule>();
    public DbSet<TicketEscalationRule> TicketEscalationRules => Set<TicketEscalationRule>();
    public DbSet<TicketWatcher> TicketWatchers => Set<TicketWatcher>();
    public DbSet<TicketRemoteSession> TicketRemoteSessions => Set<TicketRemoteSession>();
    public DbSet<TicketAutomationLink> TicketAutomationLinks => Set<TicketAutomationLink>();
    public DbSet<SlaCalendar> SlaCalendars => Set<SlaCalendar>();
    public DbSet<SlaCalendarHoliday> SlaCalendarHolidays => Set<SlaCalendarHoliday>();

    // Knowledge Base
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<KnowledgeArticleChunk> KnowledgeArticleChunks => Set<KnowledgeArticleChunk>();
    public DbSet<TicketKnowledgeLink> TicketKnowledgeLinks => Set<TicketKnowledgeLink>();
    public DbSet<KnowledgeEmbeddingQueueItem> KnowledgeEmbeddingQueueItems => Set<KnowledgeEmbeddingQueueItem>();

    // AI Provider Credentials
    public DbSet<AiProviderCredential> AiProviderCredentials => Set<AiProviderCredential>();

    // Object Storage & Attachments (genérico para qualquer escopo)
    public DbSet<Attachment> Attachments => Set<Attachment>();

    // Identity & Security
    public DbSet<User> Users => Set<User>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMembership> UserGroupMemberships => Set<UserGroupMembership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserGroupRole> UserGroupRoles => Set<UserGroupRole>();
    public DbSet<UserMfaKey> UserMfaKeys => Set<UserMfaKey>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<AuthAuditLog> AuthAuditLogs => Set<AuthAuditLog>();

    // P2P
    public DbSet<P2pAgentTelemetry> P2pAgentTelemetries => Set<P2pAgentTelemetry>();
    public DbSet<P2pArtifactPresence> P2pArtifactPresences => Set<P2pArtifactPresence>();
    public DbSet<P2pSeedPlan> P2pSeedPlans => Set<P2pSeedPlan>();
    public DbSet<AgentP2pBootstrap> AgentP2pBootstraps => Set<AgentP2pBootstrap>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDateTimeConversion(modelBuilder);
        modelBuilder.HasPostgresExtension("vector");

        // Infrastructure
        ConfigureP2pEntities(modelBuilder);
        ConfigureIdentity(modelBuilder);

        // Domain entities (in domain order)
        ConfigureCoreEntities(modelBuilder);
        ConfigureInventory(modelBuilder);
        ConfigureTickets(modelBuilder);
        ConfigureConfigurations(modelBuilder);
        ConfigureReports(modelBuilder);
        ConfigureTokensAndNotes(modelBuilder);
        ConfigureAutomation(modelBuilder);
        ConfigureCustomFields(modelBuilder);

        // AI Chat & MCP
        ConfigureAiChat(modelBuilder);

        // Attachments
        ConfigureAttachments(modelBuilder);
    }

    // ── Infrastructure (defined in partial files) ─────────────────────────

    static partial void ConfigureDateTimeConversion(ModelBuilder modelBuilder);
    static partial void ConfigureP2pEntities(ModelBuilder modelBuilder);
    static partial void ConfigureIdentity(ModelBuilder modelBuilder);
    static partial void ConfigureCoreEntities(ModelBuilder modelBuilder);
    static partial void ConfigureInventory(ModelBuilder modelBuilder);
    static partial void ConfigureTickets(ModelBuilder modelBuilder);
    static partial void ConfigureConfigurations(ModelBuilder modelBuilder);
    static partial void ConfigureReports(ModelBuilder modelBuilder);
    static partial void ConfigureTokensAndNotes(ModelBuilder modelBuilder);
    static partial void ConfigureAutomation(ModelBuilder modelBuilder);
    static partial void ConfigureCustomFields(ModelBuilder modelBuilder);
    static partial void ConfigureAiChat(ModelBuilder modelBuilder);
    static partial void ConfigureAttachments(ModelBuilder modelBuilder);
}
