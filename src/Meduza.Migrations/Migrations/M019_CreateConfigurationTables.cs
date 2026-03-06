using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260304_019)]
public class M019_CreateConfigurationTables : Migration
{
    public override void Up()
    {
        // ============ server_configurations (singleton) ============
        Create.Table("server_configurations")
            .WithColumn("id").AsGuid().PrimaryKey()
            // Funcionalidades
            .WithColumn("recovery_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("discovery_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("p2p_files_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("support_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("knowledge_base_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            // Loja
            .WithColumn("app_store_policy").AsInt32().NotNullable().WithDefaultValue(1) // PreApproved
            // Inventário e updates
            .WithColumn("inventory_interval_hours").AsInt32().NotNullable().WithDefaultValue(24)
            .WithColumn("auto_update_settings_json").AsCustom("text").NotNullable().WithDefaultValue("")
            // Token / Agent
            .WithColumn("token_expiration_days").AsInt32().NotNullable().WithDefaultValue(365)
            .WithColumn("max_tokens_per_agent").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("agent_heartbeat_interval_seconds").AsInt32().NotNullable().WithDefaultValue(60)
            .WithColumn("agent_offline_threshold_seconds").AsInt32().NotNullable().WithDefaultValue(300)
            // Branding e IA
            .WithColumn("branding_settings_json").AsCustom("text").NotNullable().WithDefaultValue("")
            .WithColumn("ai_integration_settings_json").AsCustom("text").NotNullable().WithDefaultValue("")
            // Locks de herança (JSON array de nomes de propriedades)
            .WithColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]")
            // Auditoria
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable()
            .WithColumn("version").AsInt32().NotNullable().WithDefaultValue(1);

        // ============ client_configurations ============
        Create.Table("client_configurations")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable().Unique()
                .ForeignKey("fk_client_configurations_client", "clients", "id").OnDelete(System.Data.Rule.Cascade)
            // Funcionalidades (nullable = herda servidor)
            .WithColumn("recovery_enabled").AsBoolean().Nullable()
            .WithColumn("discovery_enabled").AsBoolean().Nullable()
            .WithColumn("p2p_files_enabled").AsBoolean().Nullable()
            .WithColumn("support_enabled").AsBoolean().Nullable()
            // Loja e IA (nullable = herda servidor)
            .WithColumn("app_store_policy").AsInt32().Nullable()
            .WithColumn("ai_integration_settings_json").AsCustom("text").Nullable()
            // Inventário e updates
            .WithColumn("inventory_interval_hours").AsInt32().Nullable()
            .WithColumn("auto_update_settings_json").AsCustom("text").Nullable()
            // Token / Agent (nullable = herda servidor)
            .WithColumn("token_expiration_days").AsInt32().Nullable()
            .WithColumn("max_tokens_per_agent").AsInt32().Nullable()
            .WithColumn("agent_heartbeat_interval_seconds").AsInt32().Nullable()
            .WithColumn("agent_offline_threshold_seconds").AsInt32().Nullable()
            // Locks de herança no nível cliente
            .WithColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]")
            // Auditoria
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable()
            .WithColumn("version").AsInt32().NotNullable().WithDefaultValue(1);

        // ============ site_configurations ============
        Create.Table("site_configurations")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("site_id").AsGuid().NotNullable().Unique()
                .ForeignKey("fk_site_configurations_site", "sites", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("client_id").AsGuid().NotNullable()
                .ForeignKey("fk_site_configurations_client", "clients", "id")
            // Funcionalidades (nullable = herda cliente/servidor)
            .WithColumn("recovery_enabled").AsBoolean().Nullable()
            .WithColumn("discovery_enabled").AsBoolean().Nullable()
            .WithColumn("p2p_files_enabled").AsBoolean().Nullable()
            .WithColumn("support_enabled").AsBoolean().Nullable()
            // Loja e IA (nullable = herda cliente/servidor)
            .WithColumn("app_store_policy").AsInt32().Nullable()
            .WithColumn("ai_integration_settings_json").AsCustom("text").Nullable()
            // Inventário e updates
            .WithColumn("inventory_interval_hours").AsInt32().Nullable()
            .WithColumn("auto_update_settings_json").AsCustom("text").Nullable()
            // Informações do site
            .WithColumn("timezone").AsString(100).Nullable()
            .WithColumn("location").AsString(500).Nullable()
            .WithColumn("contact_person").AsString(256).Nullable()
            .WithColumn("contact_email").AsString(256).Nullable()
            // Locks de herança no nível site
            .WithColumn("locked_fields_json").AsCustom("text").NotNullable().WithDefaultValue("[]")
            // Auditoria
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable()
            .WithColumn("version").AsInt32().NotNullable().WithDefaultValue(1);

        // ============ configuration_audits ============
        Create.Table("configuration_audits")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("entity_type").AsString(50).NotNullable()   // "Server", "Client", "Site"
            .WithColumn("entity_id").AsGuid().NotNullable()
            .WithColumn("field_name").AsString(256).NotNullable()
            .WithColumn("old_value").AsCustom("text").Nullable()
            .WithColumn("new_value").AsCustom("text").Nullable()
            .WithColumn("reason").AsString(1000).Nullable()
            .WithColumn("changed_by").AsString(256).Nullable()
            .WithColumn("changed_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("ip_address").AsString(64).Nullable()
            .WithColumn("entity_version").AsInt32().NotNullable().WithDefaultValue(1);

        Create.Index("ix_configuration_audits_entity").OnTable("configuration_audits")
            .OnColumn("entity_type").Ascending()
            .OnColumn("entity_id").Ascending()
            .OnColumn("changed_at").Descending();

    }

    public override void Down()
    {
        Delete.Table("configuration_audits");
        Delete.Table("site_configurations");
        Delete.Table("client_configurations");
        Delete.Table("server_configurations");
    }
}
