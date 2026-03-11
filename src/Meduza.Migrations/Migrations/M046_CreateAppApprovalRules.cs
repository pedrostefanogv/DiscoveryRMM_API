using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260311_046)]
public class M046_CreateAppApprovalRules : Migration
{
    public override void Up()
    {
        Create.Table("app_approval_rules")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("scope_type").AsInt32().NotNullable()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("sites", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("installation_type").AsInt32().NotNullable()
            .WithColumn("package_id").AsString(300).NotNullable()
            .WithColumn("action").AsInt32().NotNullable()
            .WithColumn("auto_update_enabled").AsBoolean().Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_app_approval_rules_scope")
            .OnTable("app_approval_rules")
            .OnColumn("scope_type").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("installation_type").Ascending()
            .OnColumn("package_id").Ascending();

        Create.Index("ux_app_approval_rules_unique")
            .OnTable("app_approval_rules")
            .OnColumn("scope_type").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("installation_type").Ascending()
            .OnColumn("package_id").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("app_approval_rules");
    }
}
