using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260413_092)]
public class M092_CreateAgentUpdateInfrastructure : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("agent_update_policy_json").AsCustom("text").NotNullable().WithDefaultValue("");

        Alter.Table("client_configurations")
            .AddColumn("agent_update_policy_json").AsCustom("text").Nullable();

        Alter.Table("site_configurations")
            .AddColumn("agent_update_policy_json").AsCustom("text").Nullable();

        Create.Table("agent_releases")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("version").AsString(64).NotNullable()
            .WithColumn("channel").AsString(32).NotNullable().WithDefaultValue("stable")
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("mandatory").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("minimum_supported_version").AsString(64).Nullable()
            .WithColumn("release_notes").AsCustom("text").Nullable()
            .WithColumn("published_at_utc").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable();

        Create.Index("ux_agent_releases_version_channel")
            .OnTable("agent_releases")
            .OnColumn("version").Ascending()
            .OnColumn("channel").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_agent_releases_channel_active_published")
            .OnTable("agent_releases")
            .OnColumn("channel").Ascending()
            .OnColumn("is_active").Ascending()
            .OnColumn("published_at_utc").Descending();

        Create.Table("agent_release_artifacts")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_release_id").AsGuid().NotNullable().ForeignKey("agent_releases", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("platform").AsString(32).NotNullable()
            .WithColumn("architecture").AsString(32).NotNullable()
            .WithColumn("artifact_type").AsInt32().NotNullable()
            .WithColumn("file_name").AsString(500).NotNullable()
            .WithColumn("content_type").AsString(200).NotNullable()
            .WithColumn("storage_object_key").AsString(1000).NotNullable()
            .WithColumn("storage_bucket").AsString(200).NotNullable()
            .WithColumn("storage_provider_type").AsInt32().NotNullable()
            .WithColumn("sha256").AsString(64).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("signature_thumbprint").AsString(200).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_agent_release_artifacts_release_platform_arch_type")
            .OnTable("agent_release_artifacts")
            .OnColumn("agent_release_id").Ascending()
            .OnColumn("platform").Ascending()
            .OnColumn("architecture").Ascending()
            .OnColumn("artifact_type").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_agent_release_artifacts_storage_object_key")
            .OnTable("agent_release_artifacts")
            .OnColumn("storage_object_key").Ascending();

        Create.Table("agent_update_events")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_release_id").AsGuid().Nullable().ForeignKey("agent_releases", "id")
            .WithColumn("event_type").AsInt32().NotNullable()
            .WithColumn("current_version").AsString(64).Nullable()
            .WithColumn("target_version").AsString(64).Nullable()
            .WithColumn("message").AsString(1000).Nullable()
            .WithColumn("correlation_id").AsString(100).Nullable()
            .WithColumn("details_json").AsCustom("jsonb").Nullable()
            .WithColumn("occurred_at_utc").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agent_update_events_agent_occurred")
            .OnTable("agent_update_events")
            .OnColumn("agent_id").Ascending()
            .OnColumn("occurred_at_utc").Descending();

        Create.Index("ix_agent_update_events_release_id")
            .OnTable("agent_update_events")
            .OnColumn("agent_release_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("agent_update_events");
        Delete.Table("agent_release_artifacts");
        Delete.Table("agent_releases");

        Delete.Column("agent_update_policy_json").FromTable("site_configurations");
        Delete.Column("agent_update_policy_json").FromTable("client_configurations");
        Delete.Column("agent_update_policy_json").FromTable("server_configurations");
    }
}
