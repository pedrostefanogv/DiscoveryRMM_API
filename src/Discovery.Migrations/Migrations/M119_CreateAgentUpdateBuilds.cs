using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260503_119)]
public class M119_CreateAgentUpdateBuilds : Migration
{
    public override void Up()
    {
        Create.Table("agent_update_builds")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("version").AsString(64).NotNullable()
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
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable();

        Create.Index("ix_agent_update_builds_active_target")
            .OnTable("agent_update_builds")
            .OnColumn("is_active").Ascending()
            .OnColumn("platform").Ascending()
            .OnColumn("architecture").Ascending()
            .OnColumn("artifact_type").Ascending()
            .OnColumn("updated_at").Descending();

        Create.Index("ix_agent_update_builds_storage_object_key")
            .OnTable("agent_update_builds")
            .OnColumn("storage_object_key").Ascending();

        Execute.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_update_builds_active_target
            ON agent_update_builds (platform, architecture, artifact_type)
            WHERE is_active = true;");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_agent_update_builds_active_target;");
        Delete.Table("agent_update_builds");
    }
}
