using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Configuration: Server, Client, Site, MeshCentral, Audit ──
//    Includes: ServerConfiguration, ClientConfiguration, SiteConfiguration,
//    ConfigurationAudit, MeshCentralRightsProfile

public partial class DiscoveryDbContext
{
    static partial void ConfigureConfigurations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServerConfiguration>(entity =>
        {
            entity.ToTable("server_configurations");
            entity.HasKey(config => config.Id);

            entity.Property(config => config.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.CloudBootstrapEnabled).HasColumnName("cloud_bootstrap_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile).HasColumnName("meshcentral_group_policy_profile").HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy).HasColumnName("app_store_policy").HasConversion<int>();
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentUpdatePolicyJson).HasColumnName("agent_update_policy_json");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.BrandingSettingsJson).HasColumnName("branding_settings_json");
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.NatsEnabled).HasColumnName("nats_enabled");
            entity.Property(config => config.NatsAuthEnabled).HasColumnName("nats_auth_enabled");
            entity.Property(config => config.NatsAgentJwtTtlMinutes).HasColumnName("nats_agent_jwt_ttl_minutes");
            entity.Property(config => config.NatsUserJwtTtlMinutes).HasColumnName("nats_user_jwt_ttl_minutes");
            entity.Property(config => config.NatsUseScopedSubjects).HasColumnName("nats_use_scoped_subjects");
            entity.Property(config => config.NatsServerHostInternal).HasColumnName("nats_server_host_internal");
            entity.Property(config => config.NatsServerHostExternal).HasColumnName("nats_server_host_external");
            entity.Property(config => config.NatsUseWssExternal).HasColumnName("nats_use_wss_external");
            entity.Property(config => config.ReportingSettingsJson).HasColumnName("reporting_settings_json").HasColumnType("jsonb");
            entity.Property(config => config.RetentionSettingsJson).HasColumnName("retention_settings_json").HasColumnType("jsonb");
            entity.Property(config => config.TicketAttachmentSettingsJson).HasColumnName("ticket_attachment_settings_json").HasColumnType("jsonb");
            entity.Property(config => config.ObjectStorageBucketName).HasColumnName("object_storage_bucket_name");
            entity.Property(config => config.ObjectStorageEndpoint).HasColumnName("object_storage_endpoint");
            entity.Property(config => config.ObjectStorageRegion).HasColumnName("object_storage_region");
            entity.Property(config => config.ObjectStorageAccessKey).HasColumnName("object_storage_access_key");
            entity.Property(config => config.ObjectStorageSecretKey).HasColumnName("object_storage_secret_key");
            entity.Property(config => config.ObjectStorageUrlTtlHours).HasColumnName("object_storage_url_ttl_hours");
            entity.Property(config => config.ObjectStorageUsePathStyle).HasColumnName("object_storage_use_path_style");
            entity.Property(config => config.ObjectStorageSslVerify).HasColumnName("object_storage_ssl_verify");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(config => config.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");
            entity.Property(config => config.CurrentEmbeddingDimensions).HasColumnName("current_embedding_dimensions");
        });

        modelBuilder.Entity<MeshCentralRightsProfile>(entity =>
        {
            entity.ToTable("meshcentral_rights_profiles");
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => profile.Name).IsUnique().HasDatabaseName("ix_meshcentral_rights_profiles_name");

            entity.Property(profile => profile.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(profile => profile.Name).HasColumnName("name").HasMaxLength(64);
            entity.Property(profile => profile.Description).HasColumnName("description").HasMaxLength(500);
            entity.Property(profile => profile.RightsMask).HasColumnName("rights_mask");
            entity.Property(profile => profile.IsSystem).HasColumnName("is_system");
            entity.Property(profile => profile.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(profile => profile.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ClientConfiguration>(entity =>
        {
            entity.ToTable("client_configurations");
            entity.HasKey(config => config.Id);
            entity.HasIndex(config => config.ClientId).IsUnique();

            entity.Property(config => config.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(config => config.ClientId).HasColumnName("client_id");
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.CloudBootstrapEnabled).HasColumnName("cloud_bootstrap_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile).HasColumnName("meshcentral_group_policy_profile").HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy).HasColumnName("app_store_policy").HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentUpdatePolicyJson).HasColumnName("agent_update_policy_json");
            entity.Property(config => config.AgentHeartbeatIntervalSeconds).HasColumnName("agent_heartbeat_interval_seconds");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(config => config.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");

            entity.HasOne<Client>().WithMany().HasForeignKey(config => config.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteConfiguration>(entity =>
        {
            entity.ToTable("site_configurations");
            entity.HasKey(config => config.Id);
            entity.HasIndex(config => config.SiteId).IsUnique();

            entity.Property(config => config.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(config => config.SiteId).HasColumnName("site_id");
            entity.Property(config => config.ClientId).HasColumnName("client_id");
            entity.Property(config => config.RecoveryEnabled).HasColumnName("recovery_enabled");
            entity.Property(config => config.DiscoveryEnabled).HasColumnName("discovery_enabled");
            entity.Property(config => config.P2PFilesEnabled).HasColumnName("p2p_files_enabled");
            entity.Property(config => config.SupportEnabled).HasColumnName("support_enabled");
            entity.Property(config => config.MeshCentralGroupPolicyProfile).HasColumnName("meshcentral_group_policy_profile").HasMaxLength(64);
            entity.Property(config => config.ChatAIEnabled).HasColumnName("chat_ai_enabled");
            entity.Property(config => config.KnowledgeBaseEnabled).HasColumnName("knowledge_base_enabled");
            entity.Property(config => config.AppStorePolicy).HasColumnName("app_store_policy").HasConversion<int?>();
            entity.Property(config => config.AIIntegrationSettingsJson).HasColumnName("ai_integration_settings_json");
            entity.Property(config => config.InventoryIntervalHours).HasColumnName("inventory_interval_hours");
            entity.Property(config => config.AutoUpdateSettingsJson).HasColumnName("auto_update_settings_json");
            entity.Property(config => config.AgentUpdatePolicyJson).HasColumnName("agent_update_policy_json");
            entity.Property(config => config.AgentOnlineGraceSeconds).HasColumnName("agent_online_grace_seconds");
            entity.Property(config => config.Timezone).HasColumnName("timezone").HasMaxLength(100);
            entity.Property(config => config.Location).HasColumnName("location").HasMaxLength(500);
            entity.Property(config => config.ContactPerson).HasColumnName("contact_person").HasMaxLength(256);
            entity.Property(config => config.ContactEmail).HasColumnName("contact_email").HasMaxLength(256);
            entity.Property(config => config.MeshCentralGroupName).HasColumnName("meshcentral_group_name").HasMaxLength(200);
            entity.Property(config => config.MeshCentralMeshId).HasColumnName("meshcentral_mesh_id").HasMaxLength(200);
            entity.Property(config => config.MeshCentralAppliedGroupPolicyProfile).HasColumnName("meshcentral_applied_group_policy_profile").HasMaxLength(64);
            entity.Property(config => config.MeshCentralAppliedGroupPolicyAt).HasColumnName("meshcentral_applied_group_policy_at").HasColumnType("timestamptz");
            entity.Property(config => config.LockedFieldsJson).HasColumnName("locked_fields_json");
            entity.Property(config => config.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(config => config.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(config => config.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(config => config.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            entity.Property(config => config.Version).HasColumnName("version");

            entity.HasOne<Site>().WithMany().HasForeignKey(config => config.SiteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Client>().WithMany().HasForeignKey(config => config.ClientId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConfigurationAudit>(entity =>
        {
            entity.ToTable("configuration_audits");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.EntityType, audit.EntityId, audit.ChangedAt }).HasDatabaseName("ix_configuration_audits_entity");

            entity.Property(audit => audit.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(audit => audit.EntityType).HasColumnName("entity_type").HasConversion<string>().HasMaxLength(50);
            entity.Property(audit => audit.EntityId).HasColumnName("entity_id");
            entity.Property(audit => audit.FieldName).HasColumnName("field_name").HasMaxLength(256);
            entity.Property(audit => audit.OldValue).HasColumnName("old_value");
            entity.Property(audit => audit.NewValue).HasColumnName("new_value");
            entity.Property(audit => audit.Reason).HasColumnName("reason").HasMaxLength(1000);
            entity.Property(audit => audit.ChangedBy).HasColumnName("changed_by").HasMaxLength(256);
            entity.Property(audit => audit.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamptz");
            entity.Property(audit => audit.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            entity.Property(audit => audit.EntityVersion).HasColumnName("entity_version");
        });
    }
}
