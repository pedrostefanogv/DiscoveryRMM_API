using Meduza.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Entities.Security;

namespace Meduza.Infrastructure.Data;

public class MeduzaDbContext(DbContextOptions<MeduzaDbContext> options) : DbContext(options)
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
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ReportTemplateHistory> ReportTemplateHistories => Set<ReportTemplateHistory>();
    public DbSet<ReportExecution> ReportExecutions => Set<ReportExecution>();
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
    public DbSet<AppPackage> AppPackages => Set<AppPackage>();
    public DbSet<ChocolateyPackage> ChocolateyPackages => Set<ChocolateyPackage>();
    public DbSet<WingetPackage> WingetPackages => Set<WingetPackage>();
    public DbSet<AgentLabelRule> AgentLabelRules => Set<AgentLabelRule>();
    public DbSet<AgentLabel> AgentLabels => Set<AgentLabel>();
    public DbSet<AgentLabelRuleMatch> AgentLabelRuleMatches => Set<AgentLabelRuleMatch>();

    // Knowledge Base
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<KnowledgeArticleChunk> KnowledgeArticleChunks => Set<KnowledgeArticleChunk>();
    public DbSet<TicketKnowledgeLink> TicketKnowledgeLinks => Set<TicketKnowledgeLink>();
    public DbSet<KnowledgeEmbeddingQueueItem> KnowledgeEmbeddingQueueItems => Set<KnowledgeEmbeddingQueueItem>();

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

    // P2P
    public DbSet<P2pAgentTelemetry> P2pAgentTelemetries => Set<P2pAgentTelemetry>();
    public DbSet<P2pArtifactPresence> P2pArtifactPresences => Set<P2pArtifactPresence>();
    public DbSet<P2pSeedPlan> P2pSeedPlans => Set<P2pSeedPlan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure global DateTime converter to ensure UTC timestamps
        ConfigureDateTimeConversion(modelBuilder);

        // Habilita suporte pgvector
        modelBuilder.HasPostgresExtension("vector");

        // P2P
        ConfigureP2pEntities(modelBuilder);

        // Configurar Attachment (genérico para múltiplos escopos)
        modelBuilder.Entity<Attachment>(entity =>
        {
            entity.ToTable("attachments");
            entity.HasKey(a => a.Id);
            
            // Índices para queries eficientes
            entity.HasIndex(a => new { a.EntityType, a.EntityId })
                .HasDatabaseName("ix_attachments_entity_type_id");
            entity.HasIndex(a => a.ClientId)
                .HasDatabaseName("ix_attachments_client_id");
            entity.HasIndex(a => a.CreatedAt)
                .HasDatabaseName("ix_attachments_created_at");
            entity.HasIndex(a => a.DeletedAt)
                .HasDatabaseName("ix_attachments_deleted_at");
            entity.HasIndex(a => a.StorageObjectKey)
                .HasDatabaseName("ix_attachments_storage_object_key");

            // Mapeamento de colunas
            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.EntityType).HasColumnName("entity_type").HasMaxLength(100);
            entity.Property(a => a.EntityId).HasColumnName("entity_id");
            entity.Property(a => a.ClientId).HasColumnName("client_id");
            entity.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(500);
            entity.Property(a => a.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(a => a.StorageObjectKey).HasColumnName("storage_object_key").HasMaxLength(1000);
            entity.Property(a => a.StorageBucket).HasColumnName("storage_bucket").HasMaxLength(200);
            entity.Property(a => a.ContentType).HasColumnName("content_type").HasMaxLength(200);
            entity.Property(a => a.SizeBytes).HasColumnName("size_bytes");
            entity.Property(a => a.StorageChecksum).HasColumnName("storage_checksum").HasMaxLength(200);
            entity.Property(a => a.StorageProviderType).HasColumnName("storage_provider_type");
            entity.Property(a => a.UploadedBy).HasColumnName("uploaded_by").HasMaxLength(256);
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(a => a.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("clients");
            entity.HasKey(client => client.Id);

            entity.Property(client => client.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(client => client.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(client => client.Notes)
                .HasColumnName("notes")
                .HasMaxLength(2000);
            entity.Property(client => client.IsActive)
                .HasColumnName("is_active");
            entity.Property(client => client.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(client => client.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Site>(entity =>
        {
            entity.ToTable("sites");
            entity.HasKey(site => site.Id);
            entity.HasIndex(site => site.ClientId)
                .HasDatabaseName("ix_sites_client_id");

            entity.Property(site => site.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(site => site.ClientId)
                .HasColumnName("client_id");
            entity.Property(site => site.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(site => site.Notes)
                .HasColumnName("notes")
                .HasMaxLength(2000);
            entity.Property(site => site.IsActive)
                .HasColumnName("is_active");
            entity.Property(site => site.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(site => site.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(site => site.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.ToTable("agents");
            entity.HasKey(agent => agent.Id);
            entity.HasIndex(agent => agent.SiteId)
                .HasDatabaseName("ix_agents_site_id");
            entity.HasIndex(agent => agent.Hostname)
                .HasDatabaseName("ix_agents_hostname");

            entity.Property(agent => agent.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(agent => agent.SiteId)
                .HasColumnName("site_id");
            entity.Property(agent => agent.Hostname)
                .HasColumnName("hostname")
                .HasMaxLength(200);
            entity.Property(agent => agent.DisplayName)
                .HasColumnName("display_name")
                .HasMaxLength(200);
            entity.Property(agent => agent.Status)
                .HasColumnName("status")
                .HasConversion<int>();
            entity.Property(agent => agent.OperatingSystem)
                .HasColumnName("operating_system")
                .HasMaxLength(200);
            entity.Property(agent => agent.OsVersion)
                .HasColumnName("os_version")
                .HasMaxLength(100);
            entity.Property(agent => agent.AgentVersion)
                .HasColumnName("agent_version")
                .HasMaxLength(50);
            entity.Property(agent => agent.LastIpAddress)
                .HasColumnName("last_ip_address")
                .HasMaxLength(45);
            entity.Property(agent => agent.MacAddress)
                .HasColumnName("mac_address")
                .HasMaxLength(17);
            entity.Property(agent => agent.LastSeenAt)
                .HasColumnName("last_seen_at")
                .HasColumnType("timestamptz");
            entity.Property(agent => agent.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(agent => agent.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(agent => agent.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentCommand>(entity =>
        {
            entity.ToTable("agent_commands");
            entity.HasKey(command => command.Id);
            entity.HasIndex(command => command.AgentId)
                .HasDatabaseName("ix_commands_agent_id");
            entity.HasIndex(command => command.Status)
                .HasDatabaseName("ix_commands_status");

            entity.Property(command => command.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(command => command.AgentId)
                .HasColumnName("agent_id");
            entity.Property(command => command.CommandType)
                .HasColumnName("command_type")
                .HasConversion<int>();
            entity.Property(command => command.Payload)
                .HasColumnName("payload");
            entity.Property(command => command.Status)
                .HasColumnName("status")
                .HasConversion<int>();
            entity.Property(command => command.Result)
                .HasColumnName("result");
            entity.Property(command => command.ExitCode)
                .HasColumnName("exit_code");
            entity.Property(command => command.ErrorMessage)
                .HasColumnName("error_message");
            entity.Property(command => command.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(command => command.SentAt)
                .HasColumnName("sent_at")
                .HasColumnType("timestamptz");
            entity.Property(command => command.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(command => command.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentHardwareInfo>(entity =>
        {
            entity.ToTable("agent_hardware_info");
            entity.HasKey(info => info.Id);
            entity.HasIndex(info => info.AgentId)
                .IsUnique()
                .HasDatabaseName("ix_hardware_agent_id");

            entity.Property(info => info.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(info => info.AgentId).HasColumnName("agent_id");
            entity.Property(info => info.InventoryRaw)
                .HasColumnName("inventory_raw")
                .HasColumnType("jsonb");
            entity.Property(info => info.HardwareComponentsJson)
                .HasColumnName("hardware_components_json")
                .HasColumnType("jsonb");
            entity.Property(info => info.InventorySchemaVersion)
                .HasColumnName("inventory_schema_version")
                .HasMaxLength(50);
            entity.Property(info => info.InventoryCollectedAt)
                .HasColumnName("inventory_collected_at")
                .HasColumnType("timestamptz");
            entity.Property(info => info.Manufacturer)
                .HasColumnName("manufacturer")
                .HasMaxLength(200);
            entity.Property(info => info.Model)
                .HasColumnName("model")
                .HasMaxLength(200);
            entity.Property(info => info.SerialNumber)
                .HasColumnName("serial_number")
                .HasMaxLength(100);
            entity.Property(info => info.MotherboardManufacturer)
                .HasColumnName("motherboard_manufacturer")
                .HasMaxLength(200);
            entity.Property(info => info.MotherboardModel)
                .HasColumnName("motherboard_model")
                .HasMaxLength(200);
            entity.Property(info => info.MotherboardSerialNumber)
                .HasColumnName("motherboard_serial_number")
                .HasMaxLength(100);
            entity.Property(info => info.Processor)
                .HasColumnName("processor")
                .HasMaxLength(300);
            entity.Property(info => info.ProcessorCores).HasColumnName("processor_cores");
            entity.Property(info => info.ProcessorThreads).HasColumnName("processor_threads");
            entity.Property(info => info.ProcessorArchitecture)
                .HasColumnName("processor_architecture")
                .HasMaxLength(20);
            entity.Property(info => info.TotalMemoryBytes).HasColumnName("total_memory_bytes");
            entity.Property(info => info.BiosVersion)
                .HasColumnName("bios_version")
                .HasMaxLength(200);
            entity.Property(info => info.BiosManufacturer)
                .HasColumnName("bios_manufacturer")
                .HasMaxLength(200);
            entity.Property(info => info.BiosDate)
                .HasColumnName("bios_date")
                .HasMaxLength(50);
            entity.Property(info => info.BiosSerialNumber)
                .HasColumnName("bios_serial_number")
                .HasMaxLength(100);
            entity.Property(info => info.OsName)
                .HasColumnName("os_name")
                .HasMaxLength(200);
            entity.Property(info => info.OsVersion)
                .HasColumnName("os_version")
                .HasMaxLength(100);
            entity.Property(info => info.OsBuild)
                .HasColumnName("os_build")
                .HasMaxLength(100);
            entity.Property(info => info.OsArchitecture)
                .HasColumnName("os_architecture")
                .HasMaxLength(20);
            entity.Property(info => info.ProcessorTdpWatts).HasColumnName("processor_tdp_watts");
            entity.Property(info => info.ProcessorSocket)
                .HasColumnName("processor_socket")
                .HasMaxLength(50);
            entity.Property(info => info.ProcessorFrequencyGhz).HasColumnName("processor_frequency_ghz");
            entity.Property(info => info.ProcessorReleaseDate)
                .HasColumnName("processor_release_date")
                .HasMaxLength(50);
            entity.Property(info => info.GpuModel)
                .HasColumnName("gpu_model")
                .HasMaxLength(300);
            entity.Property(info => info.GpuMemoryBytes).HasColumnName("gpu_memory_bytes");
            entity.Property(info => info.GpuDriverVersion)
                .HasColumnName("gpu_driver_version")
                .HasMaxLength(100);
            entity.Property(info => info.TotalDisksCount).HasColumnName("total_disks_count");
            entity.Property(info => info.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");
            entity.Property(info => info.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(info => info.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LogEntry>(entity =>
        {
            entity.ToTable("logs");
            entity.HasKey(log => log.Id);
            entity.HasIndex(log => log.ClientId).HasDatabaseName("ix_logs_client_id");
            entity.HasIndex(log => log.SiteId).HasDatabaseName("ix_logs_site_id");
            entity.HasIndex(log => log.AgentId).HasDatabaseName("ix_logs_agent_id");
            entity.HasIndex(log => log.Type).HasDatabaseName("ix_logs_type");
            entity.HasIndex(log => log.Level).HasDatabaseName("ix_logs_level");
            entity.HasIndex(log => log.CreatedAt).HasDatabaseName("ix_logs_created_at");

            entity.Property(log => log.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(log => log.ClientId).HasColumnName("client_id");
            entity.Property(log => log.SiteId).HasColumnName("site_id");
            entity.Property(log => log.AgentId).HasColumnName("agent_id");
            entity.Property(log => log.Type)
                .HasColumnName("log_type")
                .HasConversion<int>();
            entity.Property(log => log.Level)
                .HasColumnName("log_level")
                .HasConversion<int>();
            entity.Property(log => log.Source)
                .HasColumnName("log_source")
                .HasConversion<int>();
            entity.Property(log => log.Message).HasColumnName("message");
            entity.Property(log => log.DataJson)
                .HasColumnName("data_json")
                .HasColumnType("jsonb");
            entity.Property(log => log.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(log => log.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(log => log.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(log => log.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SoftwareCatalog>(entity =>
        {
            entity.ToTable("software_catalog");
            entity.HasKey(catalog => catalog.Id);
            entity.HasIndex(catalog => catalog.Fingerprint)
                .IsUnique();

            entity.Property(catalog => catalog.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(catalog => catalog.Name)
                .HasColumnName("name")
                .HasMaxLength(300);
            entity.Property(catalog => catalog.Publisher)
                .HasColumnName("publisher")
                .HasMaxLength(300);
            entity.Property(catalog => catalog.InstallId)
                .HasColumnName("install_id")
                .HasMaxLength(1000);
            entity.Property(catalog => catalog.Serial)
                .HasColumnName("serial")
                .HasMaxLength(1000);
            entity.Property(catalog => catalog.Source)
                .HasColumnName("source")
                .HasMaxLength(120);
            entity.Property(catalog => catalog.Fingerprint)
                .HasColumnName("fingerprint")
                .HasMaxLength(64);
            entity.Property(catalog => catalog.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(catalog => catalog.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AgentSoftwareInventory>(entity =>
        {
            entity.ToTable("agent_software_inventory");
            entity.HasKey(inventory => inventory.Id);
            entity.HasIndex(inventory => new { inventory.AgentId, inventory.SoftwareId })
                .HasDatabaseName("ix_agent_software_inventory_agent_software");
            entity.HasIndex(inventory => new { inventory.AgentId, inventory.IsPresent })
                .HasDatabaseName("ix_agent_software_inventory_agent_present");

            entity.Property(inventory => inventory.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(inventory => inventory.AgentId)
                .HasColumnName("agent_id");
            entity.Property(inventory => inventory.SoftwareId)
                .HasColumnName("software_id");
            entity.Property(inventory => inventory.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.FirstSeenAt)
                .HasColumnName("first_seen_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.LastSeenAt)
                .HasColumnName("last_seen_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.Version)
                .HasColumnName("version")
                .HasMaxLength(120);
            entity.Property(inventory => inventory.IsPresent)
                .HasColumnName("is_present");
            entity.Property(inventory => inventory.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(inventory => inventory.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SoftwareCatalog>()
                .WithMany()
                .HasForeignKey(inventory => inventory.SoftwareId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentLabelRule>(entity =>
        {
            entity.ToTable("agent_label_rules");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => rule.IsEnabled)
                .HasDatabaseName("ix_agent_label_rules_is_enabled");
            entity.HasIndex(rule => rule.Label)
                .HasDatabaseName("ix_agent_label_rules_label");

            entity.Property(rule => rule.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(rule => rule.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(rule => rule.Label)
                .HasColumnName("label")
                .HasMaxLength(120);
            entity.Property(rule => rule.Description)
                .HasColumnName("description")
                .HasMaxLength(2000);
            entity.Property(rule => rule.IsEnabled)
                .HasColumnName("is_enabled");
            entity.Property(rule => rule.ApplyMode)
                .HasColumnName("apply_mode")
                .HasConversion<int>();
            entity.Property(rule => rule.ExpressionJson)
                .HasColumnName("expression_json")
                .HasColumnType("jsonb");
            entity.Property(rule => rule.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
            entity.Property(rule => rule.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256);
            entity.Property(rule => rule.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(rule => rule.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AgentLabel>(entity =>
        {
            entity.ToTable("agent_labels");
            entity.HasKey(label => label.Id);
            entity.HasIndex(label => new { label.AgentId, label.Label })
                .IsUnique()
                .HasDatabaseName("ux_agent_labels_agent_label");

            entity.Property(label => label.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(label => label.AgentId)
                .HasColumnName("agent_id");
            entity.Property(label => label.Label)
                .HasColumnName("label")
                .HasMaxLength(120);
            entity.Property(label => label.SourceType)
                .HasColumnName("source_type")
                .HasConversion<int>();
            entity.Property(label => label.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(label => label.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(label => label.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentLabelRuleMatch>(entity =>
        {
            entity.ToTable("agent_label_rule_matches");
            entity.HasKey(match => match.Id);
            entity.HasIndex(match => new { match.RuleId, match.AgentId })
                .IsUnique()
                .HasDatabaseName("ux_agent_label_rule_matches_rule_agent");
            entity.HasIndex(match => new { match.AgentId, match.Label })
                .HasDatabaseName("ix_agent_label_rule_matches_agent_label");

            entity.Property(match => match.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(match => match.RuleId)
                .HasColumnName("rule_id");
            entity.Property(match => match.AgentId)
                .HasColumnName("agent_id");
            entity.Property(match => match.Label)
                .HasColumnName("label")
                .HasMaxLength(120);
            entity.Property(match => match.MatchedAt)
                .HasColumnName("matched_at")
                .HasColumnType("timestamptz");
            entity.Property(match => match.LastEvaluatedAt)
                .HasColumnName("last_evaluated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<AgentLabelRule>()
                .WithMany()
                .HasForeignKey(match => match.RuleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(match => match.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServerConfiguration>(entity =>
        {
            entity.ToTable("server_configurations");
            entity.HasKey(config => config.Id);

            entity.Property(config => config.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile)
                .HasColumnName("meshcentral_group_policy_profile")
                .HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int>();
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.BrandingSettingsJson).HasColumnName("branding_settings_json");
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.NatsAuthEnabled).HasColumnName("nats_auth_enabled");
            entity.Property(config => config.NatsAccountSeed).HasColumnName("nats_account_seed");
            entity.Property(config => config.NatsAgentJwtTtlMinutes).HasColumnName("nats_agent_jwt_ttl_minutes");
            entity.Property(config => config.NatsUserJwtTtlMinutes).HasColumnName("nats_user_jwt_ttl_minutes");
            entity.Property(config => config.NatsUseScopedSubjects).HasColumnName("nats_use_scoped_subjects");
            entity.Property(config => config.NatsIncludeLegacySubjects).HasColumnName("nats_include_legacy_subjects");
            entity.Property(config => config.NatsXKeySeed).HasColumnName("nats_xkey_seed");
            entity.Property(config => config.ReportingSettingsJson).HasColumnName("reporting_settings_json");
            entity.Property(config => config.TicketAttachmentSettingsJson)
                .HasColumnName("ticket_attachment_settings_json")
                .HasColumnType("jsonb");
            entity.Property(config => config.ObjectStorageBucketName).HasColumnName("object_storage_bucket_name");
            entity.Property(config => config.ObjectStorageEndpoint).HasColumnName("object_storage_endpoint");
            entity.Property(config => config.ObjectStorageRegion).HasColumnName("object_storage_region");
            entity.Property(config => config.ObjectStorageAccessKey).HasColumnName("object_storage_access_key");
            entity.Property(config => config.ObjectStorageSecretKey).HasColumnName("object_storage_secret_key");
            entity.Property(config => config.ObjectStorageUrlTtlHours).HasColumnName("object_storage_url_ttl_hours");
            entity.Property(config => config.ObjectStorageUsePathStyle).HasColumnName("object_storage_use_path_style");
            entity.Property(config => config.ObjectStorageSslVerify).HasColumnName("object_storage_ssl_verify");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
            entity.Property(config => config.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");
        });

        modelBuilder.Entity<MeshCentralRightsProfile>(entity =>
        {
            entity.ToTable("meshcentral_rights_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.Name)
                .IsUnique()
                .HasDatabaseName("ix_meshcentral_rights_profiles_name");

            entity.Property(profile => profile.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(profile => profile.Name)
                .HasColumnName("name")
                .HasMaxLength(64);
            entity.Property(profile => profile.Description)
                .HasColumnName("description")
                .HasMaxLength(500);
            entity.Property(profile => profile.RightsMask)
                .HasColumnName("rights_mask");
            entity.Property(profile => profile.IsSystem)
                .HasColumnName("is_system");
            entity.Property(profile => profile.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(profile => profile.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ClientConfiguration>(entity =>
        {
            entity.ToTable("client_configurations");
            entity.HasKey(config => config.Id);
            entity.HasIndex(config => config.ClientId)
                .IsUnique();

            entity.Property(config => config.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(config => config.ClientId).HasColumnName("client_id");
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile)
                .HasColumnName("meshcentral_group_policy_profile")
                .HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
            entity.Property(config => config.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(config => config.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteConfiguration>(entity =>
        {
            entity.ToTable("site_configurations");
            entity.HasKey(config => config.Id);
            entity.HasIndex(config => config.SiteId)
                .IsUnique();

            entity.Property(config => config.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(config => config.SiteId).HasColumnName("site_id");
            entity.Property(config => config.ClientId).HasColumnName("client_id");
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile)
                .HasColumnName("meshcentral_group_policy_profile")
                .HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.Timezone)
                .HasColumnName("timezone")
                .HasMaxLength(100);
            entity.Property(config => config.Location)
                .HasColumnName("location")
                .HasMaxLength(500);
            entity.Property(config => config.ContactPerson)
                .HasColumnName("contact_person")
                .HasMaxLength(256);
            entity.Property(config => config.ContactEmail)
                .HasColumnName("contact_email")
                .HasMaxLength(256);
            entity.Property(config => config.MeshCentralGroupName)
                .HasColumnName("meshcentral_group_name")
                .HasMaxLength(200);
            entity.Property(config => config.MeshCentralMeshId)
                .HasColumnName("meshcentral_mesh_id")
                .HasMaxLength(200);
            entity.Property(config => config.MeshCentralAppliedGroupPolicyProfile)
                .HasColumnName("meshcentral_applied_group_policy_profile")
                .HasMaxLength(64);
            entity.Property(config => config.MeshCentralAppliedGroupPolicyAt)
                .HasColumnName("meshcentral_applied_group_policy_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
            entity.Property(config => config.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");

            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(config => config.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(config => config.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConfigurationAudit>(entity =>
        {
            entity.ToTable("configuration_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.EntityType, audit.EntityId, audit.ChangedAt })
                .HasDatabaseName("ix_configuration_audits_entity");

            entity.Property(audit => audit.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(audit => audit.EntityType)
                .HasColumnName("entity_type")
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(audit => audit.EntityId).HasColumnName("entity_id");
            entity.Property(audit => audit.FieldName)
                .HasColumnName("field_name")
                .HasMaxLength(256);
            entity.Property(audit => audit.OldValue).HasColumnName("old_value");
            entity.Property(audit => audit.NewValue).HasColumnName("new_value");
            entity.Property(audit => audit.Reason)
                .HasColumnName("reason")
                .HasMaxLength(1000);
            entity.Property(audit => audit.ChangedBy)
                .HasColumnName("changed_by")
                .HasMaxLength(256);
            entity.Property(audit => audit.ChangedAt)
                .HasColumnName("changed_at")
                .HasColumnType("timestamptz");
            entity.Property(audit => audit.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(64);
            entity.Property(audit => audit.EntityVersion).HasColumnName("entity_version");
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.ToTable("tickets");
            entity.HasKey(ticket => ticket.Id);
            entity.HasIndex(ticket => ticket.ClientId)
                .HasDatabaseName("ix_tickets_client_id");

            entity.Property(ticket => ticket.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(ticket => ticket.ClientId)
                .HasColumnName("client_id");
            entity.Property(ticket => ticket.SiteId)
                .HasColumnName("site_id");
            entity.Property(ticket => ticket.AgentId)
                .HasColumnName("agent_id");
            entity.Property(ticket => ticket.Title)
                .HasColumnName("title")
                .HasMaxLength(500);
            entity.Property(ticket => ticket.Description)
                .HasColumnName("description");
            entity.Property(ticket => ticket.WorkflowStateId)
                .HasColumnName("workflow_state_id");
            entity.Property(ticket => ticket.Priority)
                .HasColumnName("priority")
                .HasConversion<int>();
            entity.Property(ticket => ticket.DepartmentId)
                .HasColumnName("department_id");
            entity.Property(ticket => ticket.WorkflowProfileId)
                .HasColumnName("workflow_profile_id");
            entity.Property(ticket => ticket.AssignedToUserId)
                .HasColumnName("assigned_to_user_id");
            entity.Property(ticket => ticket.SlaExpiresAt)
                .HasColumnName("sla_expires_at")
                .HasColumnType("timestamptz");
            entity.Property(ticket => ticket.SlaBreached)
                .HasColumnName("sla_breached");
            entity.Property(ticket => ticket.Rating)
                .HasColumnName("rating");
            entity.Property(ticket => ticket.RatedAt)
                .HasColumnName("rated_at")
                .HasColumnType("timestamptz");
            entity.Property(ticket => ticket.RatedBy)
                .HasColumnName("rated_by")
                .HasMaxLength(255);
            entity.Property(ticket => ticket.Category)
                .HasColumnName("category")
                .HasMaxLength(100);
            entity.Property(ticket => ticket.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(ticket => ticket.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
            entity.Property(ticket => ticket.ClosedAt)
                .HasColumnName("closed_at")
                .HasColumnType("timestamptz");
            entity.Property(ticket => ticket.DeletedAt)
                .HasColumnName("deleted_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(ticket => ticket.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(ticket => ticket.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(ticket => ticket.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Department>()
                .WithMany()
                .HasForeignKey(ticket => ticket.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowProfile>()
                .WithMany()
                .HasForeignKey(ticket => ticket.WorkflowProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TicketComment>(entity =>
        {
            entity.ToTable("ticket_comments");
            entity.HasKey(comment => comment.Id);
            entity.HasIndex(comment => comment.TicketId)
                .HasDatabaseName("ix_ticket_comments_ticket_id");

            entity.Property(comment => comment.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(comment => comment.TicketId)
                .HasColumnName("ticket_id");
            entity.Property(comment => comment.Author)
                .HasColumnName("author")
                .HasMaxLength(200);
            entity.Property(comment => comment.Content)
                .HasColumnName("content");
            entity.Property(comment => comment.IsInternal)
                .HasColumnName("is_internal");
            entity.Property(comment => comment.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Ticket>()
                .WithMany()
                .HasForeignKey(comment => comment.TicketId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentToken>(entity =>
        {
            entity.ToTable("agent_tokens");
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.AgentId)
                .HasDatabaseName("ix_agent_tokens_agent_id");
            entity.HasIndex(token => token.TokenHash)
                .IsUnique();

            entity.Property(token => token.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(token => token.AgentId)
                .HasColumnName("agent_id");
            entity.Property(token => token.TokenHash)
                .HasColumnName("token_hash")
                .HasMaxLength(128);
            entity.Property(token => token.TokenPrefix)
                .HasColumnName("token_prefix")
                .HasMaxLength(12);
            entity.Property(token => token.Description)
                .HasColumnName("description")
                .HasMaxLength(500);
            entity.Property(token => token.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.LastUsedAt)
                .HasColumnName("last_used_at")
                .HasColumnType("timestamptz");

            entity.Ignore(token => token.IsRevoked);
            entity.Ignore(token => token.IsExpired);
            entity.Ignore(token => token.IsValid);

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(token => token.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeployToken>(entity =>
        {
            entity.ToTable("deploy_tokens");
            entity.HasKey(token => token.Id);
            entity.HasIndex(token => token.TokenHash)
                .IsUnique();
            entity.HasIndex(token => token.ExpiresAt)
                .HasDatabaseName("ix_deploy_tokens_expires_at");
            entity.HasIndex(token => new { token.ClientId, token.SiteId })
                .HasDatabaseName("ix_deploy_tokens_client_site");

            entity.Property(token => token.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(token => token.ClientId)
                .HasColumnName("client_id");
            entity.Property(token => token.SiteId)
                .HasColumnName("site_id");
            entity.Property(token => token.TokenHash)
                .HasColumnName("token_hash")
                .HasMaxLength(128);
            entity.Property(token => token.TokenPrefix)
                .HasColumnName("token_prefix")
                .HasMaxLength(12);
            entity.Property(token => token.Description)
                .HasColumnName("description")
                .HasMaxLength(500);
            entity.Property(token => token.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.LastUsedAt)
                .HasColumnName("last_used_at")
                .HasColumnType("timestamptz");
            entity.Property(token => token.UsedCount)
                .HasColumnName("used_count");
            entity.Property(token => token.MaxUses)
                .HasColumnName("max_uses");

            entity.Ignore(token => token.IsRevoked);
            entity.Ignore(token => token.IsExpired);
            entity.Ignore(token => token.IsDepleted);
            entity.Ignore(token => token.IsValid);

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(token => token.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(token => token.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EntityNote>(entity =>
        {
            entity.ToTable("entity_notes");
            entity.HasKey(note => note.Id);
            entity.HasIndex(note => new { note.ClientId, note.CreatedAt })
                .HasDatabaseName("ix_entity_notes_client_created_at");
            entity.HasIndex(note => new { note.SiteId, note.CreatedAt })
                .HasDatabaseName("ix_entity_notes_site_created_at");
            entity.HasIndex(note => new { note.AgentId, note.CreatedAt })
                .HasDatabaseName("ix_entity_notes_agent_created_at");

            entity.Property(note => note.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(note => note.ClientId)
                .HasColumnName("client_id");
            entity.Property(note => note.SiteId)
                .HasColumnName("site_id");
            entity.Property(note => note.AgentId)
                .HasColumnName("agent_id");
            entity.Property(note => note.Content)
                .HasColumnName("content")
                .HasMaxLength(4000);
            entity.Property(note => note.Author)
                .HasColumnName("author")
                .HasMaxLength(200);
            entity.Property(note => note.IsPinned)
                .HasColumnName("is_pinned");
            entity.Property(note => note.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(note => note.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(note => note.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(note => note.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(note => note.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowState>(entity =>
        {
            entity.ToTable("workflow_states");
            entity.HasKey(state => state.Id);
            entity.HasIndex(state => state.ClientId)
                .HasDatabaseName("ix_workflow_states_client_id");

            entity.Property(state => state.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(state => state.ClientId)
                .HasColumnName("client_id");
            entity.Property(state => state.Name)
                .HasColumnName("name")
                .HasMaxLength(100);
            entity.Property(state => state.Color)
                .HasColumnName("color")
                .HasMaxLength(7);
            entity.Property(state => state.IsInitial)
                .HasColumnName("is_initial");
            entity.Property(state => state.IsFinal)
                .HasColumnName("is_final");
            entity.Property(state => state.SortOrder)
                .HasColumnName("sort_order");
            entity.Property(state => state.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(state => state.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowTransition>(entity =>
        {
            entity.ToTable("workflow_transitions");
            entity.HasKey(transition => transition.Id);
            entity.HasIndex(transition => transition.ClientId)
                .HasDatabaseName("ix_workflow_transitions_client_id");
            entity.HasIndex(transition => transition.FromStateId)
                .HasDatabaseName("ix_workflow_transitions_from");

            entity.Property(transition => transition.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(transition => transition.ClientId)
                .HasColumnName("client_id");
            entity.Property(transition => transition.FromStateId)
                .HasColumnName("from_state_id");
            entity.Property(transition => transition.ToStateId)
                .HasColumnName("to_state_id");
            entity.Property(transition => transition.Name)
                .HasColumnName("name")
                .HasMaxLength(100);
            entity.Property(transition => transition.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(transition => transition.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowState>()
                .WithMany()
                .HasForeignKey(transition => transition.FromStateId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowState>()
                .WithMany()
                .HasForeignKey(transition => transition.ToStateId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("departments");
            entity.HasKey(dept => dept.Id);
            entity.HasIndex(dept => dept.ClientId)
                .HasDatabaseName("ix_departments_client_id");
            entity.HasIndex(dept => dept.IsActive)
                .HasDatabaseName("ix_departments_is_active");

            entity.Property(dept => dept.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(dept => dept.ClientId)
                .HasColumnName("client_id");
            entity.Property(dept => dept.Name)
                .HasColumnName("name")
                .HasMaxLength(255);
            entity.Property(dept => dept.Description)
                .HasColumnName("description")
                .HasMaxLength(1000);
            entity.Property(dept => dept.InheritFromGlobalId)
                .HasColumnName("inherit_from_global_id");
            entity.Property(dept => dept.SortOrder)
                .HasColumnName("sort_order");
            entity.Property(dept => dept.IsActive)
                .HasColumnName("is_active");
            entity.Property(dept => dept.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(dept => dept.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(dept => dept.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowProfile>(entity =>
        {
            entity.ToTable("workflow_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.DepartmentId)
                .HasDatabaseName("ix_workflow_profiles_dept");
            entity.HasIndex(profile => profile.ClientId)
                .HasDatabaseName("ix_workflow_profiles_client");

            entity.Property(profile => profile.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(profile => profile.ClientId)
                .HasColumnName("client_id");
            entity.Property(profile => profile.DepartmentId)
                .HasColumnName("department_id");
            entity.Property(profile => profile.Name)
                .HasColumnName("name")
                .HasMaxLength(255);
            entity.Property(profile => profile.Description)
                .HasColumnName("description")
                .HasMaxLength(1000);
            entity.Property(profile => profile.SlaHours)
                .HasColumnName("sla_hours");
            entity.Property(profile => profile.DefaultPriority)
                .HasColumnName("default_priority")
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(profile => profile.IsActive)
                .HasColumnName("is_active");
            entity.Property(profile => profile.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(profile => profile.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(profile => profile.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Department>()
                .WithMany()
                .HasForeignKey(profile => profile.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TicketActivityLog>(entity =>
        {
            entity.ToTable("ticket_activity_logs");
            entity.HasKey(log => log.Id);
            entity.HasIndex(log => log.TicketId)
                .HasDatabaseName("ix_ticket_activity_logs_ticket");
            entity.HasIndex(log => log.CreatedAt)
                .HasDatabaseName("ix_ticket_activity_logs_created")
                .IsDescending();
            entity.HasIndex(log => log.Type)
                .HasDatabaseName("ix_ticket_activity_logs_type");

            entity.Property(log => log.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(log => log.TicketId)
                .HasColumnName("ticket_id");
            entity.Property(log => log.Type)
                .HasColumnName("activity_type")
                .HasConversion<string>()
                .HasMaxLength(50);
            entity.Property(log => log.ChangedByUserId)
                .HasColumnName("changed_by_user_id");
            entity.Property(log => log.OldValue)
                .HasColumnName("old_value")
                .HasMaxLength(1000);
            entity.Property(log => log.NewValue)
                .HasColumnName("new_value")
                .HasMaxLength(1000);
            entity.Property(log => log.Comment)
                .HasColumnName("comment")
                .HasMaxLength(2000);
            entity.Property(log => log.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Ticket>()
                .WithMany()
                .HasForeignKey(log => log.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("report_templates");
            entity.HasKey(template => template.Id);
            entity.HasIndex(template => new { template.ClientId, template.DatasetType, template.IsActive })
                .HasDatabaseName("ix_report_templates_client_dataset_active");
            entity.HasIndex(template => template.CreatedAt)
                .HasDatabaseName("ix_report_templates_created_at");

            entity.Property(template => template.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(template => template.ClientId)
                .HasColumnName("client_id");
            entity.Property(template => template.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(template => template.Description)
                .HasColumnName("description")
                .HasMaxLength(2000);
            entity.Property(template => template.Instructions)
                .HasColumnName("instructions")
                .HasMaxLength(4000);
            entity.Property(template => template.ExecutionSchemaJson)
                .HasColumnName("execution_schema_json")
                .HasColumnType("jsonb");
            entity.Property(template => template.DatasetType)
                .HasColumnName("dataset_type")
                .HasConversion<int>();
            entity.Property(template => template.DefaultFormat)
                .HasColumnName("default_format")
                .HasConversion<int>();
            entity.Property(template => template.LayoutJson)
                .HasColumnName("layout_json")
                .HasColumnType("jsonb");
            entity.Property(template => template.FiltersJson)
                .HasColumnName("filters_json")
                .HasColumnType("jsonb");
            entity.Property(template => template.IsActive)
                .HasColumnName("is_active");
            entity.Property(template => template.Version)
                .HasColumnName("version");
            entity.Property(template => template.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(template => template.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
            entity.Property(template => template.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
            entity.Property(template => template.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(256);

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(template => template.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReportExecution>(entity =>
        {
            entity.ToTable("report_executions");
            entity.HasKey(execution => execution.Id);
            entity.HasIndex(execution => new { execution.ClientId, execution.Status, execution.CreatedAt })
                .HasDatabaseName("ix_report_executions_client_status_created");
            entity.HasIndex(execution => execution.TemplateId)
                .HasDatabaseName("ix_report_executions_template");

            entity.Property(execution => execution.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(execution => execution.TemplateId)
                .HasColumnName("template_id");
            entity.Property(execution => execution.ClientId)
                .HasColumnName("client_id");
            entity.Property(execution => execution.Format)
                .HasColumnName("format")
                .HasConversion<int>();
            entity.Property(execution => execution.FiltersJson)
                .HasColumnName("filters_json")
                .HasColumnType("jsonb");
            entity.Property(execution => execution.Status)
                .HasColumnName("status")
                .HasConversion<int>();
            entity.Property(execution => execution.StorageProviderType)
                .HasColumnName("storage_provider_type");
            entity.Property(execution => execution.StorageBucket)
                .HasColumnName("storage_bucket")
                .HasMaxLength(200);
            entity.Property(execution => execution.StorageObjectKey)
                .HasColumnName("storage_object_key")
                .HasMaxLength(1000);
            entity.Property(execution => execution.StorageContentType)
                .HasColumnName("storage_content_type")
                .HasMaxLength(200);
            entity.Property(execution => execution.StorageSizeBytes)
                .HasColumnName("storage_size_bytes");
            entity.Property(execution => execution.StorageChecksum)
                .HasColumnName("storage_checksum")
                .HasMaxLength(200);
            entity.Property(execution => execution.StoragePresignedUrl)
                .HasColumnName("storage_presigned_url")
                .HasMaxLength(2000);
            entity.Property(execution => execution.StoragePresignedUrlGeneratedAt)
                .HasColumnName("storage_presigned_url_generated_at")
                .HasColumnType("timestamptz");
            entity.Property(execution => execution.RowCount)
                .HasColumnName("row_count");
            entity.Property(execution => execution.ErrorMessage)
                .HasColumnName("error_message")
                .HasMaxLength(2000);
            entity.Property(execution => execution.ExecutionTimeMs)
                .HasColumnName("execution_time_ms");
            entity.Property(execution => execution.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(execution => execution.StartedAt)
                .HasColumnName("started_at")
                .HasColumnType("timestamptz");
            entity.Property(execution => execution.FinishedAt)
                .HasColumnName("finished_at")
                .HasColumnType("timestamptz");
            entity.Property(execution => execution.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);

            entity.HasOne<ReportTemplate>()
                .WithMany()
                .HasForeignKey(execution => execution.TemplateId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(execution => execution.ClientId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReportTemplateHistory>(entity =>
        {
            entity.ToTable("report_template_history");
            entity.HasKey(history => history.Id);
            entity.HasIndex(history => new { history.TemplateId, history.Version })
                .HasDatabaseName("ix_report_template_history_template_version");
            entity.HasIndex(history => history.ChangedAt)
                .HasDatabaseName("ix_report_template_history_changed_at");

            entity.Property(history => history.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(history => history.TemplateId)
                .HasColumnName("template_id");
            entity.Property(history => history.Version)
                .HasColumnName("version");
            entity.Property(history => history.ChangeType)
                .HasColumnName("change_type")
                .HasMaxLength(32);
            entity.Property(history => history.SnapshotJson)
                .HasColumnName("snapshot_json")
                .HasColumnType("jsonb");
            entity.Property(history => history.ChangedAt)
                .HasColumnName("changed_at")
                .HasColumnType("timestamptz");
            entity.Property(history => history.ChangedBy)
                .HasColumnName("changed_by")
                .HasMaxLength(256);
        });

        modelBuilder.Entity<AppNotification>(entity =>
        {
            entity.ToTable("app_notifications");
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => notification.CreatedAt)
                .HasDatabaseName("ix_app_notifications_created_at");
            entity.HasIndex(notification => new { notification.Topic, notification.CreatedAt })
                .HasDatabaseName("ix_app_notifications_topic_created");
            entity.HasIndex(notification => new { notification.RecipientUserId, notification.IsRead, notification.CreatedAt })
                .HasDatabaseName("ix_app_notifications_user_read_created");
            entity.HasIndex(notification => new { notification.RecipientAgentId, notification.IsRead, notification.CreatedAt })
                .HasDatabaseName("ix_app_notifications_agent_read_created");
            entity.HasIndex(notification => new { notification.RecipientKey, notification.IsRead, notification.CreatedAt })
                .HasDatabaseName("ix_app_notifications_key_read_created");

            entity.Property(notification => notification.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(notification => notification.EventType)
                .HasColumnName("event_type")
                .HasMaxLength(120);
            entity.Property(notification => notification.Topic)
                .HasColumnName("topic")
                .HasMaxLength(120);
            entity.Property(notification => notification.Severity)
                .HasColumnName("severity")
                .HasConversion<int>();
            entity.Property(notification => notification.RecipientUserId)
                .HasColumnName("recipient_user_id");
            entity.Property(notification => notification.RecipientAgentId)
                .HasColumnName("recipient_agent_id");
            entity.Property(notification => notification.RecipientKey)
                .HasColumnName("recipient_key")
                .HasMaxLength(256);
            entity.Property(notification => notification.Title)
                .HasColumnName("title")
                .HasMaxLength(200);
            entity.Property(notification => notification.Message)
                .HasColumnName("message")
                .HasMaxLength(2000);
            entity.Property(notification => notification.PayloadJson)
                .HasColumnName("payload_json")
                .HasColumnType("jsonb");
            entity.Property(notification => notification.IsRead)
                .HasColumnName("is_read");
            entity.Property(notification => notification.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(notification => notification.ReadAt)
                .HasColumnName("read_at")
                .HasColumnType("timestamptz");
            entity.Property(notification => notification.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(256);
        });

        modelBuilder.Entity<AppApprovalRule>(entity =>
        {
            entity.ToTable("app_approval_rules");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => new
                {
                    rule.ScopeType,
                    rule.ClientId,
                    rule.SiteId,
                    rule.AgentId,
                    rule.InstallationType,
                    rule.PackageId
                })
                .HasDatabaseName("ux_app_approval_rules_unique")
                .IsUnique();

            entity.Property(rule => rule.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(rule => rule.ScopeType)
                .HasColumnName("scope_type")
                .HasConversion<int>();
            entity.Property(rule => rule.ClientId)
                .HasColumnName("client_id");
            entity.Property(rule => rule.SiteId)
                .HasColumnName("site_id");
            entity.Property(rule => rule.AgentId)
                .HasColumnName("agent_id");
            entity.Property(rule => rule.InstallationType)
                .HasColumnName("installation_type")
                .HasConversion<int>();
            entity.Property(rule => rule.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(rule => rule.Action)
                .HasColumnName("action")
                .HasConversion<int>();
            entity.Property(rule => rule.AutoUpdateEnabled)
                .HasColumnName("auto_update_enabled");
            entity.Property(rule => rule.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(rule => rule.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(rule => rule.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(rule => rule.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(rule => rule.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppApprovalAudit>(entity =>
        {
            entity.ToTable("app_approval_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.InstallationType, audit.PackageId, audit.ChangedAt })
                .HasDatabaseName("ix_app_approval_audits_package_changed");

            entity.Property(audit => audit.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(audit => audit.RuleId)
                .HasColumnName("rule_id");
            entity.Property(audit => audit.ChangeType)
                .HasColumnName("change_type")
                .HasConversion<int>();
            entity.Property(audit => audit.ScopeType)
                .HasColumnName("scope_type")
                .HasConversion<int>();
            entity.Property(audit => audit.ClientId)
                .HasColumnName("client_id");
            entity.Property(audit => audit.SiteId)
                .HasColumnName("site_id");
            entity.Property(audit => audit.AgentId)
                .HasColumnName("agent_id");
            entity.Property(audit => audit.InstallationType)
                .HasColumnName("installation_type")
                .HasConversion<int>();
            entity.Property(audit => audit.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(audit => audit.OldAction)
                .HasColumnName("old_action")
                .HasConversion<int?>();
            entity.Property(audit => audit.NewAction)
                .HasColumnName("new_action")
                .HasConversion<int?>();
            entity.Property(audit => audit.OldAutoUpdateEnabled)
                .HasColumnName("old_auto_update_enabled");
            entity.Property(audit => audit.NewAutoUpdateEnabled)
                .HasColumnName("new_auto_update_enabled");
            entity.Property(audit => audit.Reason)
                .HasColumnName("reason")
                .HasMaxLength(2000);
            entity.Property(audit => audit.ChangedBy)
                .HasColumnName("changed_by")
                .HasMaxLength(256);
            entity.Property(audit => audit.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(64);
            entity.Property(audit => audit.ChangedAt)
                .HasColumnName("changed_at")
                .HasColumnType("timestamptz");

            entity.HasOne<AppApprovalRule>()
                .WithMany()
                .HasForeignKey(audit => audit.RuleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AutomationScriptDefinition>(entity =>
        {
            entity.ToTable("automation_script_definitions");
            entity.HasKey(script => script.Id);
            entity.HasIndex(script => new { script.ClientId, script.IsActive, script.UpdatedAt })
                .HasDatabaseName("ix_automation_scripts_client_active_updated");
            entity.HasIndex(script => new { script.Name, script.Version })
                .HasDatabaseName("ix_automation_scripts_name_version");

            entity.Property(script => script.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(script => script.ClientId)
                .HasColumnName("client_id");
            entity.Property(script => script.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(script => script.Summary)
                .HasColumnName("summary")
                .HasMaxLength(2000);
            entity.Property(script => script.ScriptType)
                .HasColumnName("script_type")
                .HasConversion<int>();
            entity.Property(script => script.Version)
                .HasColumnName("version")
                .HasMaxLength(50);
            entity.Property(script => script.ExecutionFrequency)
                .HasColumnName("execution_frequency")
                .HasMaxLength(100);
            entity.Property(script => script.TriggerModesJson)
                .HasColumnName("trigger_modes_json")
                .HasColumnType("jsonb");
            entity.Property(script => script.Content)
                .HasColumnName("content")
                .HasColumnType("text");
            entity.Property(script => script.ParametersSchemaJson)
                .HasColumnName("parameters_schema_json")
                .HasColumnType("jsonb");
            entity.Property(script => script.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb");
            entity.Property(script => script.IsActive)
                .HasColumnName("is_active");
            entity.Property(script => script.LastUpdatedAt)
                .HasColumnName("last_updated_at")
                .HasColumnType("timestamptz");
            entity.Property(script => script.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(script => script.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(script => script.ClientId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AutomationScriptAudit>(entity =>
        {
            entity.ToTable("automation_script_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.ScriptId, audit.ChangedAt })
                .HasDatabaseName("ix_automation_script_audits_script_changed");

            entity.Property(audit => audit.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(audit => audit.ScriptId)
                .HasColumnName("script_id");
            entity.Property(audit => audit.ChangeType)
                .HasColumnName("change_type")
                .HasConversion<int>();
            entity.Property(audit => audit.Reason)
                .HasColumnName("reason")
                .HasMaxLength(2000);
            entity.Property(audit => audit.OldValueJson)
                .HasColumnName("old_value_json")
                .HasColumnType("jsonb");
            entity.Property(audit => audit.NewValueJson)
                .HasColumnName("new_value_json")
                .HasColumnType("jsonb");
            entity.Property(audit => audit.ChangedBy)
                .HasColumnName("changed_by")
                .HasMaxLength(256);
            entity.Property(audit => audit.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(64);
            entity.Property(audit => audit.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(64);
            entity.Property(audit => audit.ChangedAt)
                .HasColumnName("changed_at")
                .HasColumnType("timestamptz");

            entity.HasOne<AutomationScriptDefinition>()
                .WithMany()
                .HasForeignKey(audit => audit.ScriptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationTaskDefinition>(entity =>
        {
            entity.ToTable("automation_task_definitions");
            entity.HasKey(task => task.Id);
            entity.HasIndex(task => new { task.ScopeType, task.ClientId, task.SiteId, task.AgentId })
                .HasDatabaseName("ix_automation_tasks_scope");
            entity.HasIndex(task => new { task.IsActive, task.UpdatedAt })
                .HasDatabaseName("ix_automation_tasks_active_updated");

            entity.Property(task => task.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(task => task.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(task => task.Description)
                .HasColumnName("description")
                .HasMaxLength(2000);
            entity.Property(task => task.ActionType)
                .HasColumnName("action_type")
                .HasConversion<int>();
            entity.Property(task => task.InstallationType)
                .HasColumnName("installation_type")
                .HasConversion<int?>();
            entity.Property(task => task.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(task => task.ScriptId)
                .HasColumnName("script_id");
            entity.Property(task => task.CommandPayload)
                .HasColumnName("command_payload")
                .HasColumnType("text");
            entity.Property(task => task.ScopeType)
                .HasColumnName("scope_type")
                .HasConversion<int>();
            entity.Property(task => task.ClientId)
                .HasColumnName("client_id");
            entity.Property(task => task.SiteId)
                .HasColumnName("site_id");
            entity.Property(task => task.AgentId)
                .HasColumnName("agent_id");
            entity.Property(task => task.IncludeTagsJson)
                .HasColumnName("include_tags_json")
                .HasColumnType("jsonb");
            entity.Property(task => task.ExcludeTagsJson)
                .HasColumnName("exclude_tags_json")
                .HasColumnType("jsonb");
            entity.Property(task => task.TriggerImmediate)
                .HasColumnName("trigger_immediate");
            entity.Property(task => task.TriggerRecurring)
                .HasColumnName("trigger_recurring");
            entity.Property(task => task.TriggerOnUserLogin)
                .HasColumnName("trigger_on_user_login");
            entity.Property(task => task.TriggerOnAgentCheckIn)
                .HasColumnName("trigger_on_agent_check_in");
            entity.Property(task => task.ScheduleCron)
                .HasColumnName("schedule_cron")
                .HasMaxLength(100);
            entity.Property(task => task.RequiresApproval)
                .HasColumnName("requires_approval");
            entity.Property(task => task.IsActive)
                .HasColumnName("is_active");
            entity.Property(task => task.DeletedAt)
                .HasColumnName("deleted_at")
                .HasColumnType("timestamptz");
            entity.Property(task => task.LastUpdatedAt)
                .HasColumnName("last_updated_at")
                .HasColumnType("timestamptz");
            entity.Property(task => task.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(task => task.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(task => task.ClientId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(task => task.SiteId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(task => task.AgentId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationScriptDefinition>()
                .WithMany()
                .HasForeignKey(task => task.ScriptId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AutomationTaskAudit>(entity =>
        {
            entity.ToTable("automation_task_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.TaskId, audit.ChangedAt })
                .HasDatabaseName("ix_automation_task_audits_task_changed");

            entity.Property(audit => audit.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(audit => audit.TaskId)
                .HasColumnName("task_id");
            entity.Property(audit => audit.ChangeType)
                .HasColumnName("change_type")
                .HasConversion<int>();
            entity.Property(audit => audit.Reason)
                .HasColumnName("reason")
                .HasMaxLength(2000);
            entity.Property(audit => audit.OldValueJson)
                .HasColumnName("old_value_json")
                .HasColumnType("jsonb");
            entity.Property(audit => audit.NewValueJson)
                .HasColumnName("new_value_json")
                .HasColumnType("jsonb");
            entity.Property(audit => audit.ChangedBy)
                .HasColumnName("changed_by")
                .HasMaxLength(256);
            entity.Property(audit => audit.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(64);
            entity.Property(audit => audit.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(64);
            entity.Property(audit => audit.ChangedAt)
                .HasColumnName("changed_at")
                .HasColumnType("timestamptz");

            entity.HasOne<AutomationTaskDefinition>()
                .WithMany()
                .HasForeignKey(audit => audit.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutomationExecutionReport>(entity =>
        {
            entity.ToTable("automation_execution_reports");
            entity.HasKey(report => report.Id);
            entity.HasIndex(report => report.CommandId)
                .HasDatabaseName("ux_automation_execution_reports_command")
                .IsUnique();
            entity.HasIndex(report => new { report.AgentId, report.CreatedAt })
                .HasDatabaseName("ix_automation_execution_reports_agent_created");

            entity.Property(report => report.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(report => report.CommandId).HasColumnName("command_id");
            entity.Property(report => report.AgentId).HasColumnName("agent_id");
            entity.Property(report => report.TaskId).HasColumnName("task_id");
            entity.Property(report => report.ScriptId).HasColumnName("script_id");
            entity.Property(report => report.SourceType).HasColumnName("source_type").HasConversion<int>();
            entity.Property(report => report.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(report => report.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            entity.Property(report => report.RequestMetadataJson).HasColumnName("request_metadata_json").HasColumnType("jsonb");
            entity.Property(report => report.AckMetadataJson).HasColumnName("ack_metadata_json").HasColumnType("jsonb");
            entity.Property(report => report.ResultMetadataJson).HasColumnName("result_metadata_json").HasColumnType("jsonb");
            entity.Property(report => report.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            entity.Property(report => report.ResultReceivedAt).HasColumnName("result_received_at").HasColumnType("timestamptz");
            entity.Property(report => report.ExitCode).HasColumnName("exit_code");
            entity.Property(report => report.ErrorMessage).HasColumnName("error_message").HasMaxLength(4000);
            entity.Property(report => report.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(report => report.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(report => report.AgentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AgentCommand>().WithMany().HasForeignKey(report => report.CommandId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AutomationTaskDefinition>().WithMany().HasForeignKey(report => report.TaskId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<AutomationScriptDefinition>().WithMany().HasForeignKey(report => report.ScriptId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SyncPingDelivery>(entity =>
        {
            entity.ToTable("sync_ping_deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.HasIndex(delivery => new { delivery.EventId, delivery.AgentId, delivery.Revision })
                .HasDatabaseName("ux_sync_ping_deliveries_event_agent_revision")
                .IsUnique();
            entity.HasIndex(delivery => new { delivery.Status, delivery.SentAt })
                .HasDatabaseName("ix_sync_ping_deliveries_status_sent");
            entity.HasIndex(delivery => new { delivery.AgentId, delivery.CreatedAt })
                .HasDatabaseName("ix_sync_ping_deliveries_agent_created");

            entity.Property(delivery => delivery.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(delivery => delivery.EventId).HasColumnName("event_id");
            entity.Property(delivery => delivery.AgentId).HasColumnName("agent_id");
            entity.Property(delivery => delivery.Resource).HasColumnName("resource").HasConversion<int>();
            entity.Property(delivery => delivery.Revision).HasColumnName("revision").HasMaxLength(255);
            entity.Property(delivery => delivery.Status).HasColumnName("status").HasConversion<int>();
            entity.Property(delivery => delivery.SentAt).HasColumnName("sent_at").HasColumnType("timestamptz");
            entity.Property(delivery => delivery.AcknowledgedAt).HasColumnName("acknowledged_at").HasColumnType("timestamptz");
            entity.Property(delivery => delivery.AckMetadataJson).HasColumnName("ack_metadata_json").HasColumnType("jsonb");
            entity.Property(delivery => delivery.ErrorCode).HasColumnName("error_code").HasMaxLength(100);
            entity.Property(delivery => delivery.ErrorMessage).HasColumnName("error_message").HasMaxLength(1000);
            entity.Property(delivery => delivery.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(delivery => delivery.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>().WithMany().HasForeignKey(delivery => delivery.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppPackage>(entity =>
        {
            entity.ToTable("app_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => new { p.InstallationType, p.PackageId })
                .HasDatabaseName("ux_app_packages_installation_package")
                .IsUnique();

            entity.Property(p => p.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(p => p.InstallationType)
                .HasColumnName("installation_type")
                .HasConversion<int>();
            entity.Property(p => p.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(p => p.Name)
                .HasColumnName("name")
                .HasMaxLength(500);
            entity.Property(p => p.Publisher)
                .HasColumnName("publisher")
                .HasMaxLength(500);
            entity.Property(p => p.Version)
                .HasColumnName("version")
                .HasMaxLength(100);
            entity.Property(p => p.Description)
                .HasColumnName("description")
                .HasColumnType("text");
            entity.Property(p => p.IconUrl)
                .HasColumnName("icon_url")
                .HasMaxLength(2000);
            entity.Property(p => p.SiteUrl)
                .HasColumnName("site_url")
                .HasMaxLength(2000);
            entity.Property(p => p.InstallCommand)
                .HasColumnName("install_command")
                .HasMaxLength(1000);
            entity.Property(p => p.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb");
            entity.Property(p => p.FileObjectKey)
                .HasColumnName("file_object_key")
                .HasMaxLength(1000);
            entity.Property(p => p.FileBucket)
                .HasColumnName("file_bucket")
                .HasMaxLength(200);
            entity.Property(p => p.FilePublicUrl)
                .HasColumnName("file_public_url")
                .HasMaxLength(2000);
            entity.Property(p => p.FileContentType)
                .HasColumnName("file_content_type")
                .HasMaxLength(200);
            entity.Property(p => p.FileSizeBytes)
                .HasColumnName("file_size_bytes");
            entity.Property(p => p.FileChecksum)
                .HasColumnName("file_checksum")
                .HasMaxLength(200);
            entity.Property(p => p.SourceGeneratedAt)
                .HasColumnName("source_generated_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.LastUpdated)
                .HasColumnName("last_updated")
                .HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt)
                .HasColumnName("synced_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ChocolateyPackage>(entity =>
        {
            entity.ToTable("chocolatey_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PackageId)
                .HasDatabaseName("ux_chocolatey_packages_package_id")
                .IsUnique();

            entity.Property(p => p.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(p => p.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(p => p.Name)
                .HasColumnName("name")
                .HasMaxLength(500);
            entity.Property(p => p.Publisher)
                .HasColumnName("publisher")
                .HasMaxLength(500);
            entity.Property(p => p.Version)
                .HasColumnName("version")
                .HasMaxLength(100);
            entity.Property(p => p.Description)
                .HasColumnName("description")
                .HasColumnType("text");
            entity.Property(p => p.Homepage)
                .HasColumnName("homepage")
                .HasMaxLength(2000);
            entity.Property(p => p.LicenseUrl)
                .HasColumnName("license_url")
                .HasMaxLength(2000);
            entity.Property(p => p.Tags)
                .HasColumnName("tags")
                .HasMaxLength(2000);
            entity.Property(p => p.DownloadCount)
                .HasColumnName("download_count");
            entity.Property(p => p.LastUpdated)
                .HasColumnName("last_updated")
                .HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt)
                .HasColumnName("synced_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<WingetPackage>(entity =>
        {
            entity.ToTable("winget_packages");
            entity.HasKey(p => p.Id);
            entity.HasIndex(p => p.PackageId)
                .HasDatabaseName("ux_winget_packages_package_id")
                .IsUnique();

            entity.Property(p => p.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(p => p.PackageId)
                .HasColumnName("package_id")
                .HasMaxLength(300);
            entity.Property(p => p.Name)
                .HasColumnName("name")
                .HasMaxLength(500);
            entity.Property(p => p.Publisher)
                .HasColumnName("publisher")
                .HasMaxLength(500);
            entity.Property(p => p.Version)
                .HasColumnName("version")
                .HasMaxLength(100);
            entity.Property(p => p.Description)
                .HasColumnName("description")
                .HasColumnType("text");
            entity.Property(p => p.Homepage)
                .HasColumnName("homepage")
                .HasMaxLength(2000);
            entity.Property(p => p.License)
                .HasColumnName("license")
                .HasMaxLength(500);
            entity.Property(p => p.Category)
                .HasColumnName("category")
                .HasMaxLength(250);
            entity.Property(p => p.Icon)
                .HasColumnName("icon")
                .HasMaxLength(2000);
            entity.Property(p => p.InstallCommand)
                .HasColumnName("install_command")
                .HasMaxLength(1000);
            entity.Property(p => p.Tags)
                .HasColumnName("tags")
                .HasColumnType("text");
            entity.Property(p => p.InstallerUrlsJson)
                .HasColumnName("installer_urls_json")
                .HasColumnType("text");
            entity.Property(p => p.LastUpdated)
                .HasColumnName("last_updated")
                .HasColumnType("timestamptz");
            entity.Property(p => p.SourceGeneratedAt)
                .HasColumnName("source_generated_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.SyncedAt)
                .HasColumnName("synced_at")
                .HasColumnType("timestamptz");
            entity.Property(p => p.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
        });

        // ── Knowledge Base ─────────────────────────────────────────────

        modelBuilder.Entity<KnowledgeArticle>(entity =>
        {
            entity.ToTable("knowledge_articles");
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.ClientId).HasDatabaseName("ix_ka_client_id");
            entity.HasIndex(a => a.SiteId).HasDatabaseName("ix_ka_site_id");
            entity.HasIndex(a => a.IsPublished).HasDatabaseName("ix_ka_is_published");
            entity.HasIndex(a => a.DeletedAt).HasDatabaseName("ix_ka_deleted_at");

            entity.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(a => a.ClientId).HasColumnName("client_id");
            entity.Property(a => a.SiteId).HasColumnName("site_id");
            entity.Property(a => a.Title).HasColumnName("title").HasMaxLength(500);
            entity.Property(a => a.Content).HasColumnName("content");
            entity.Property(a => a.Category).HasColumnName("category").HasMaxLength(200);
            entity.Property(a => a.TagsJson).HasColumnName("tags_json").HasColumnType("jsonb");
            entity.Property(a => a.Author).HasColumnName("author").HasMaxLength(256);
            entity.Property(a => a.IsPublished).HasColumnName("is_published");
            entity.Property(a => a.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            entity.Property(a => a.LastChunkedAt).HasColumnName("last_chunked_at").HasColumnType("timestamptz");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(a => a.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");

            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Site>()
                .WithMany()
                .HasForeignKey(a => a.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KnowledgeArticleChunk>(entity =>
        {
            entity.ToTable("knowledge_article_chunks");
            entity.HasKey(c => c.Id);
            entity.HasIndex(c => c.ArticleId).HasDatabaseName("ix_kac_article_id");
            entity.HasIndex(c => c.EmbeddingGeneratedAt).HasDatabaseName("ix_kac_no_embedding");

            entity.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(c => c.ArticleId).HasColumnName("article_id");
            entity.Property(c => c.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(c => c.SectionTitle).HasColumnName("section_title").HasMaxLength(500);
            entity.Property(c => c.Content).HasColumnName("content");
            entity.Property(c => c.TokenCount).HasColumnName("token_count");
            entity.Property(c => c.Embedding).HasColumnName("embedding").HasColumnType("vector(1536)");
            entity.Property(c => c.EmbeddingGeneratedAt).HasColumnName("embedding_generated_at").HasColumnType("timestamptz");

            entity.HasOne(c => c.Article)
                .WithMany(a => a.Chunks)
                .HasForeignKey(c => c.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketKnowledgeLink>(entity =>
        {
            entity.ToTable("ticket_knowledge_links");
            entity.HasKey(l => l.Id);
            entity.HasIndex(l => l.TicketId).HasDatabaseName("ix_tkl_ticket_id");
            entity.HasIndex(l => new { l.TicketId, l.ArticleId })
                .IsUnique()
                .HasDatabaseName("uq_tkl_ticket_article");

            entity.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(l => l.TicketId).HasColumnName("ticket_id");
            entity.Property(l => l.ArticleId).HasColumnName("article_id");
            entity.Property(l => l.LinkedBy).HasColumnName("linked_by").HasMaxLength(256);
            entity.Property(l => l.Note).HasColumnName("note").HasMaxLength(2000);
            entity.Property(l => l.LinkedAt).HasColumnName("linked_at").HasColumnType("timestamptz");

            entity.HasOne(l => l.Ticket)
                .WithMany()
                .HasForeignKey(l => l.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(l => l.Article)
                .WithMany(a => a.TicketLinks)
                .HasForeignKey(l => l.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeEmbeddingQueueItem>(entity =>
        {
            entity.ToTable("knowledge_embedding_queue");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ArticleId)
                .IsUnique()
                .HasDatabaseName("ux_knowledge_embedding_queue_article");
            entity.HasIndex(e => new { e.Status, e.AvailableAt })
                .HasDatabaseName("ix_knowledge_embedding_queue_status_available");

            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.ArticleId).HasColumnName("article_id");
            entity.Property(e => e.Status).HasColumnName("status").HasMaxLength(20);
            entity.Property(e => e.Attempts).HasColumnName("attempts");
            entity.Property(e => e.AvailableAt).HasColumnName("available_at").HasColumnType("timestamptz");
            entity.Property(e => e.LastError).HasColumnName("last_error");
            entity.Property(e => e.Reason).HasColumnName("reason").HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<KnowledgeArticle>()
                .WithMany()
                .HasForeignKey(e => e.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    ConfigureIdentity(modelBuilder);
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(u => u.Login).HasColumnName("login").HasMaxLength(100);
            e.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
            e.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(256);
            e.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(256);
            e.Property(u => u.PasswordSalt).HasColumnName("password_salt").HasMaxLength(64);
            e.Property(u => u.IsActive).HasColumnName("is_active");
            e.Property(u => u.MfaRequired).HasColumnName("mfa_required");
            e.Property(u => u.MfaConfigured).HasColumnName("mfa_configured");
            e.Property(u => u.MustChangePassword).HasColumnName("must_change_password");
            e.Property(u => u.MustChangeProfile).HasColumnName("must_change_profile");
            e.Property(u => u.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(u => u.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(u => u.LastLoginAt).HasColumnName("last_login_at").HasColumnType("timestamptz");
            e.Property(u => u.MeshCentralUserId).HasColumnName("meshcentral_user_id").HasMaxLength(256);
            e.Property(u => u.MeshCentralUsername).HasColumnName("meshcentral_username").HasMaxLength(100);
            e.Property(u => u.MeshCentralLastSyncedAt).HasColumnName("meshcentral_last_synced_at").HasColumnType("timestamptz");
            e.Property(u => u.MeshCentralSyncStatus).HasColumnName("meshcentral_sync_status").HasMaxLength(32);
            e.Property(u => u.MeshCentralSyncError).HasColumnName("meshcentral_sync_error").HasMaxLength(1024);
            e.HasIndex(u => u.Login).IsUnique().HasDatabaseName("ix_users_login");
            e.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ix_users_email");
            e.HasIndex(u => u.MeshCentralUserId).HasDatabaseName("ix_users_meshcentral_user_id");
        });

        modelBuilder.Entity<UserGroup>(e =>
        {
            e.ToTable("user_groups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(g => g.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(g => g.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(g => g.IsActive).HasColumnName("is_active");
            e.Property(g => g.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(g => g.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserGroupMembership>(e =>
        {
            e.ToTable("user_group_memberships");
            e.HasKey(m => new { m.UserId, m.GroupId });
            e.Property(m => m.UserId).HasColumnName("user_id");
            e.Property(m => m.GroupId).HasColumnName("group_id");
            e.Property(m => m.JoinedAt).HasColumnName("joined_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(r => r.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(r => r.Type).HasColumnName("type").HasConversion<string>();
            e.Property(r => r.IsSystem).HasColumnName("is_system");
            e.Property(r => r.MfaRequirement).HasColumnName("mfa_requirement").HasConversion<string>();
            e.Property(r => r.MeshRightsMask).HasColumnName("mesh_rights_mask");
            e.Property(r => r.MeshRightsProfile).HasColumnName("mesh_rights_profile").HasMaxLength(64);
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(p => p.ResourceType).HasColumnName("resource_type").HasConversion<string>();
            e.Property(p => p.ActionType).HasColumnName("action_type").HasConversion<string>();
            e.Property(p => p.Description).HasColumnName("description").HasMaxLength(500);
            e.HasIndex(p => new { p.ResourceType, p.ActionType }).IsUnique().HasDatabaseName("ix_permissions_resource_action");
        });

        modelBuilder.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(rp => new { rp.RoleId, rp.PermissionId });
            e.Property(rp => rp.RoleId).HasColumnName("role_id");
            e.Property(rp => rp.PermissionId).HasColumnName("permission_id");
        });

        modelBuilder.Entity<UserGroupRole>(e =>
        {
            e.ToTable("user_group_roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.GroupId).HasColumnName("group_id");
            e.Property(r => r.RoleId).HasColumnName("role_id");
            e.Property(r => r.ScopeLevel).HasColumnName("scope_level").HasConversion<string>();
            e.Property(r => r.ScopeId).HasColumnName("scope_id");
            e.Property(r => r.AssignedAt).HasColumnName("assigned_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserMfaKey>(e =>
        {
            e.ToTable("user_mfa_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(k => k.UserId).HasColumnName("user_id");
            e.Property(k => k.KeyType).HasColumnName("key_type").HasConversion<string>();
            e.Property(k => k.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(k => k.IsActive).HasColumnName("is_active");
            e.Property(k => k.CredentialIdBase64).HasColumnName("credential_id_base64").HasMaxLength(512);
            e.Property(k => k.PublicKeyBase64).HasColumnName("public_key_base64").HasMaxLength(2048);
            e.Property(k => k.SignCount).HasColumnName("sign_count");
            e.Property(k => k.AaguidBase64).HasColumnName("aaguid_base64").HasMaxLength(64);
            e.Property(k => k.UserHandleBase64).HasColumnName("user_handle_base64").HasMaxLength(128);
            e.Property(k => k.OtpSecretEncrypted).HasColumnName("otp_secret_encrypted").HasMaxLength(512);
            e.Property(k => k.BackupCodeHashes).HasColumnName("backup_code_hashes").HasColumnType("text[]");
            e.Property(k => k.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(k => k.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.AccessTokenHash).HasColumnName("access_token_hash").HasMaxLength(128);
            e.Property(s => s.RefreshTokenHash).HasColumnName("refresh_token_hash").HasMaxLength(128);
            e.Property(s => s.MfaVerified).HasColumnName("mfa_verified");
            e.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            e.Property(s => s.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            e.HasIndex(s => s.RefreshTokenHash).HasDatabaseName("ix_user_sessions_refresh_token_hash");
            e.HasIndex(s => new { s.UserId, s.RevokedAt }).HasDatabaseName("ix_user_sessions_user_active");
        });

        modelBuilder.Entity<ApiToken>(e =>
        {
            e.ToTable("api_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(t => t.UserId).HasColumnName("user_id");
            e.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(t => t.TokenIdPublic).HasColumnName("token_id_public").HasMaxLength(50);
            e.Property(t => t.AccessKeyHash).HasColumnName("access_key_hash").HasMaxLength(128);
            e.Property(t => t.IsActive).HasColumnName("is_active");
            e.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(t => t.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(t => t.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.HasIndex(t => t.TokenIdPublic).IsUnique().HasDatabaseName("ix_api_tokens_token_id_public");
            e.HasIndex(t => t.UserId).HasDatabaseName("ix_api_tokens_user_id");
        });
    }

    private static void ConfigureP2pEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<P2pAgentTelemetry>(e =>
        {
            e.ToTable("p2p_agent_telemetry");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(t => t.AgentId).HasColumnName("agent_id");
            e.Property(t => t.SiteId).HasColumnName("site_id");
            e.Property(t => t.ClientId).HasColumnName("client_id");
            e.Property(t => t.CollectedAt).HasColumnName("collected_at").HasColumnType("timestamptz");
            e.Property(t => t.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz");
            e.Property(t => t.PublishedArtifacts).HasColumnName("published_artifacts");
            e.Property(t => t.ReplicationsStarted).HasColumnName("replications_started");
            e.Property(t => t.ReplicationsSucceeded).HasColumnName("replications_succeeded");
            e.Property(t => t.ReplicationsFailed).HasColumnName("replications_failed");
            e.Property(t => t.BytesServed).HasColumnName("bytes_served");
            e.Property(t => t.BytesDownloaded).HasColumnName("bytes_downloaded");
            e.Property(t => t.QueuedReplications).HasColumnName("queued_replications");
            e.Property(t => t.ActiveReplications).HasColumnName("active_replications");
            e.Property(t => t.AutoDistributionRuns).HasColumnName("auto_distribution_runs");
            e.Property(t => t.CatalogRefreshRuns).HasColumnName("catalog_refresh_runs");
            e.Property(t => t.ChunkedDownloads).HasColumnName("chunked_downloads");
            e.Property(t => t.ChunksDownloaded).HasColumnName("chunks_downloaded");
            e.Property(t => t.PlanTotalAgents).HasColumnName("plan_total_agents");
            e.Property(t => t.PlanConfiguredPercent).HasColumnName("plan_configured_percent");
            e.Property(t => t.PlanMinSeeds).HasColumnName("plan_min_seeds");
            e.Property(t => t.PlanSelectedSeeds).HasColumnName("plan_selected_seeds");

            e.HasIndex(t => new { t.AgentId, t.CollectedAt })
                .HasDatabaseName("ix_p2p_telemetry_agent_time");
            e.HasIndex(t => new { t.SiteId, t.CollectedAt })
                .HasDatabaseName("ix_p2p_telemetry_site_time");
            e.HasIndex(t => new { t.ClientId, t.CollectedAt })
                .HasDatabaseName("ix_p2p_telemetry_client_time");
        });

        modelBuilder.Entity<P2pArtifactPresence>(e =>
        {
            e.ToTable("p2p_artifact_presence");
            e.HasKey(p => new { p.ArtifactId, p.AgentId });
            e.Property(p => p.ArtifactId).HasColumnName("artifact_id").HasMaxLength(512);
            e.Property(p => p.AgentId).HasColumnName("agent_id");
            e.Property(p => p.SiteId).HasColumnName("site_id");
            e.Property(p => p.ClientId).HasColumnName("client_id");
            e.Property(p => p.ArtifactName).HasColumnName("artifact_name").HasMaxLength(260);
            e.Property(p => p.IdIsSynthetic).HasColumnName("id_is_synthetic");
            e.Property(p => p.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamptz");

            e.HasIndex(p => new { p.ArtifactId, p.LastSeenAt })
                .HasDatabaseName("ix_p2p_presence_artifact_time");
            e.HasIndex(p => new { p.SiteId, p.LastSeenAt })
                .HasDatabaseName("ix_p2p_presence_site_time");
            e.HasIndex(p => new { p.ClientId, p.LastSeenAt })
                .HasDatabaseName("ix_p2p_presence_client_time");
        });

        modelBuilder.Entity<P2pSeedPlan>(e =>
        {
            e.ToTable("p2p_seed_plan");
            e.HasKey(p => p.SiteId);
            e.Property(p => p.SiteId).HasColumnName("site_id").ValueGeneratedNever();
            e.Property(p => p.ClientId).HasColumnName("client_id");
            e.Property(p => p.TotalAgents).HasColumnName("total_agents");
            e.Property(p => p.ConfiguredPercent).HasColumnName("configured_percent");
            e.Property(p => p.MinSeeds).HasColumnName("min_seeds");
            e.Property(p => p.SelectedSeeds).HasColumnName("selected_seeds");
            e.Property(p => p.GeneratedAt).HasColumnName("generated_at").HasColumnType("timestamptz");

            e.HasIndex(p => p.ClientId).HasDatabaseName("ix_p2p_seed_plan_client");
        });
    }

    private static void ConfigureDateTimeConversion(ModelBuilder modelBuilder)
    {
        var dateTimeConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var nullableDateTimeConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : null,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                {
                    property.SetValueConverter(dateTimeConverter);
                }
                else if (property.ClrType == typeof(DateTime?))
                {
                    property.SetValueConverter(nullableDateTimeConverter);
                }
            }
        }
    }
}