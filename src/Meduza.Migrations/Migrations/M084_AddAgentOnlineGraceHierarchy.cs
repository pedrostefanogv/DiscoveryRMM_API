using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260319_084)]
public class M084_AddAgentOnlineGraceHierarchy : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("agent_online_grace_seconds").Exists())
            Alter.Table("server_configurations")
                .AddColumn("agent_online_grace_seconds")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(120);

        if (!Schema.Table("client_configurations").Column("agent_online_grace_seconds").Exists())
            Alter.Table("client_configurations")
                .AddColumn("agent_online_grace_seconds")
                .AsInt32()
                .Nullable();

        if (!Schema.Table("site_configurations").Column("agent_online_grace_seconds").Exists())
            Alter.Table("site_configurations")
                .AddColumn("agent_online_grace_seconds")
                .AsInt32()
                .Nullable();
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Column("agent_online_grace_seconds").Exists())
            Delete.Column("agent_online_grace_seconds").FromTable("site_configurations");

        if (Schema.Table("client_configurations").Column("agent_online_grace_seconds").Exists())
            Delete.Column("agent_online_grace_seconds").FromTable("client_configurations");

        if (Schema.Table("server_configurations").Column("agent_online_grace_seconds").Exists())
            Delete.Column("agent_online_grace_seconds").FromTable("server_configurations");
    }
}
