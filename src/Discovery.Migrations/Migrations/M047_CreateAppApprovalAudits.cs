using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_047)]
public class M047_CreateAppApprovalAudits : Migration
{
    public override void Up()
    {
        Create.Table("app_approval_audits")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("rule_id").AsGuid().Nullable().ForeignKey("app_approval_rules", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("change_type").AsInt32().NotNullable()
            .WithColumn("scope_type").AsInt32().NotNullable()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("sites", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("agents", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("installation_type").AsInt32().NotNullable()
            .WithColumn("package_id").AsString(300).NotNullable()
            .WithColumn("old_action").AsInt32().Nullable()
            .WithColumn("new_action").AsInt32().Nullable()
            .WithColumn("old_auto_update_enabled").AsBoolean().Nullable()
            .WithColumn("new_auto_update_enabled").AsBoolean().Nullable()
            .WithColumn("reason").AsString(2000).Nullable()
            .WithColumn("changed_by").AsString(256).Nullable()
            .WithColumn("ip_address").AsString(64).Nullable()
            .WithColumn("changed_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_app_approval_audits_package_changed")
            .OnTable("app_approval_audits")
            .OnColumn("installation_type").Ascending()
            .OnColumn("package_id").Ascending()
            .OnColumn("changed_at").Descending();

        Create.Index("ix_app_approval_audits_scope_changed")
            .OnTable("app_approval_audits")
            .OnColumn("scope_type").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending()
            .OnColumn("changed_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("app_approval_audits");
    }
}
