using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_011)]
public class M011_CreateLogs : Migration
{
    public override void Up()
    {
        Create.Table("logs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_logs_client", "clients", "id")
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_logs_site", "sites", "id")
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("fk_logs_agent", "agents", "id")
            .WithColumn("log_type").AsInt32().NotNullable()
            .WithColumn("log_level").AsInt32().NotNullable()
            .WithColumn("log_source").AsInt32().NotNullable()
            .WithColumn("message").AsString(int.MaxValue).NotNullable()
            .WithColumn("data_json").AsCustom("jsonb").Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_logs_client_id").OnTable("logs").OnColumn("client_id");
        Create.Index("ix_logs_site_id").OnTable("logs").OnColumn("site_id");
        Create.Index("ix_logs_agent_id").OnTable("logs").OnColumn("agent_id");
        Create.Index("ix_logs_type").OnTable("logs").OnColumn("log_type");
        Create.Index("ix_logs_level").OnTable("logs").OnColumn("log_level");
        Create.Index("ix_logs_created_at").OnTable("logs").OnColumn("created_at");
    }

    public override void Down()
    {
        Delete.Table("logs");
    }
}
