using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Core domain entities: Client, Site, Agent, AgentCommand, AgentHardwareInfo, LogEntry ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureCoreEntities(ModelBuilder modelBuilder)
    {
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
            entity.HasIndex(agent => agent.MeshCentralNodeId)
                .HasDatabaseName("ix_agents_meshcentral_node_id");

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
            entity.Property(agent => agent.MeshCentralNodeId)
                .HasColumnName("meshcentral_node_id")
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
            entity.Property(agent => agent.ZeroTouchPending)
                .HasColumnName("zero_touch_pending")
                .HasDefaultValue(false);

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
    }
}
