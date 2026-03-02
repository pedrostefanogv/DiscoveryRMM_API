using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_007)]
public class M007_CreateAgentTokensAndTenantSettings : Migration
{
    public override void Up()
    {
        Create.Table("tenant_settings")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable().Unique().ForeignKey("fk_tenant_settings_client", "clients", "id")
            .WithColumn("token_expiration_days").AsInt32().NotNullable().WithDefaultValue(365)
            .WithColumn("max_tokens_per_agent").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("agent_heartbeat_interval_seconds").AsInt32().NotNullable().WithDefaultValue(60)
            .WithColumn("agent_offline_threshold_seconds").AsInt32().NotNullable().WithDefaultValue(300)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("agent_tokens")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_agent_tokens_agent", "agents", "id")
            .WithColumn("token_hash").AsString(128).NotNullable().Unique()
            .WithColumn("token_prefix").AsString(12).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("revoked_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_used_at").AsDateTimeOffset().Nullable();

        Create.Index("ix_agent_tokens_agent_id").OnTable("agent_tokens").OnColumn("agent_id");
        Create.Index("ix_agent_tokens_token_hash").OnTable("agent_tokens").OnColumn("token_hash");
    }

    public override void Down()
    {
        Delete.Table("agent_tokens");
        Delete.Table("tenant_settings");
    }
}
