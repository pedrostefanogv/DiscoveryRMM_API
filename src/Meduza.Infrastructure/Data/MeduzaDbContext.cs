using Meduza.Core.Entities;
using Microsoft.EntityFrameworkCore;

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
    public DbSet<DiskInfo> DiskInfos => Set<DiskInfo>();
    public DbSet<LogEntry> Logs => Set<LogEntry>();
    public DbSet<MemoryModuleInfo> MemoryModuleInfos => Set<MemoryModuleInfo>();
    public DbSet<NetworkAdapterInfo> NetworkAdapterInfos => Set<NetworkAdapterInfo>();
    public DbSet<ServerConfiguration> ServerConfigurations => Set<ServerConfiguration>();
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure global DateTime converter to ensure UTC timestamps
        ConfigureDateTimeConversion(modelBuilder);

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

        modelBuilder.Entity<DiskInfo>(entity =>
        {
            entity.ToTable("disk_info");
            entity.HasKey(disk => disk.Id);
            entity.HasIndex(disk => disk.AgentId)
                .HasDatabaseName("ix_disk_info_agent_id");

            entity.Property(disk => disk.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(disk => disk.AgentId).HasColumnName("agent_id");
            entity.Property(disk => disk.DriveLetter)
                .HasColumnName("drive_letter")
                .HasMaxLength(10);
            entity.Property(disk => disk.Label)
                .HasColumnName("label")
                .HasMaxLength(200);
            entity.Property(disk => disk.FileSystem)
                .HasColumnName("file_system")
                .HasMaxLength(50);
            entity.Property(disk => disk.TotalSizeBytes).HasColumnName("total_size_bytes");
            entity.Property(disk => disk.FreeSpaceBytes).HasColumnName("free_space_bytes");
            entity.Property(disk => disk.MediaType)
                .HasColumnName("media_type")
                .HasMaxLength(50);
            entity.Property(disk => disk.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(disk => disk.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NetworkAdapterInfo>(entity =>
        {
            entity.ToTable("network_adapter_info");
            entity.HasKey(adapter => adapter.Id);
            entity.HasIndex(adapter => adapter.AgentId)
                .HasDatabaseName("ix_network_adapter_agent_id");

            entity.Property(adapter => adapter.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(adapter => adapter.AgentId).HasColumnName("agent_id");
            entity.Property(adapter => adapter.Name)
                .HasColumnName("name")
                .HasMaxLength(200);
            entity.Property(adapter => adapter.MacAddress)
                .HasColumnName("mac_address")
                .HasMaxLength(17);
            entity.Property(adapter => adapter.IpAddress)
                .HasColumnName("ip_address")
                .HasMaxLength(45);
            entity.Property(adapter => adapter.SubnetMask)
                .HasColumnName("subnet_mask")
                .HasMaxLength(45);
            entity.Property(adapter => adapter.Gateway)
                .HasColumnName("gateway")
                .HasMaxLength(45);
            entity.Property(adapter => adapter.DnsServers)
                .HasColumnName("dns_servers")
                .HasMaxLength(500);
            entity.Property(adapter => adapter.IsDhcpEnabled).HasColumnName("is_dhcp_enabled");
            entity.Property(adapter => adapter.AdapterType)
                .HasColumnName("adapter_type")
                .HasMaxLength(50);
            entity.Property(adapter => adapter.Speed)
                .HasColumnName("speed")
                .HasMaxLength(50);
            entity.Property(adapter => adapter.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(adapter => adapter.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MemoryModuleInfo>(entity =>
        {
            entity.ToTable("memory_module_info");
            entity.HasKey(module => module.Id);
            entity.HasIndex(module => module.AgentId)
                .HasDatabaseName("ix_memory_module_agent_id");

            entity.Property(module => module.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(module => module.AgentId).HasColumnName("agent_id");
            entity.Property(module => module.Slot)
                .HasColumnName("slot")
                .HasMaxLength(50);
            entity.Property(module => module.CapacityBytes).HasColumnName("capacity_bytes");
            entity.Property(module => module.SpeedMhz).HasColumnName("speed_mhz");
            entity.Property(module => module.MemoryType)
                .HasColumnName("memory_type")
                .HasMaxLength(50);
            entity.Property(module => module.Manufacturer)
                .HasColumnName("manufacturer")
                .HasMaxLength(200);
            entity.Property(module => module.PartNumber)
                .HasColumnName("part_number")
                .HasMaxLength(100);
            entity.Property(module => module.SerialNumber)
                .HasColumnName("serial_number")
                .HasMaxLength(100);
            entity.Property(module => module.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(module => module.AgentId)
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
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int>();
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.TokenExpirationDays).HasColumnName("token_expiration_days");
            entity.Property(config => config.MaxTokensPerAgent).HasColumnName("max_tokens_per_agent");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOfflineThresholdSeconds).HasColumnName("agent_offline_threshold_seconds");
            entity.Property(config => config.BrandingSettingsJson).HasColumnName("branding_settings_json");
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
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
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.TokenExpirationDays).HasColumnName("token_expiration_days");
            entity.Property(config => config.MaxTokensPerAgent).HasColumnName("max_tokens_per_agent");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOfflineThresholdSeconds).HasColumnName("agent_offline_threshold_seconds");
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
            entity.Property(config => config.AppStorePolicy)
                .HasColumnName("app_store_policy")
                .HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
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