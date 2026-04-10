using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_078)]
public class M078_AddMeshCentralGroupPolicyConfiguration : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("meshcentral_group_policy_profile").AsString(64).NotNullable().WithDefaultValue("viewer");

        Alter.Table("client_configurations")
            .AddColumn("meshcentral_group_policy_profile").AsString(64).Nullable();

        Alter.Table("site_configurations")
            .AddColumn("meshcentral_group_policy_profile").AsString(64).Nullable()
            .AddColumn("meshcentral_applied_group_policy_profile").AsString(64).Nullable()
            .AddColumn("meshcentral_applied_group_policy_at").AsDateTimeOffset().Nullable();
    }

    public override void Down()
    {
        Delete.Column("meshcentral_applied_group_policy_at").FromTable("site_configurations");
        Delete.Column("meshcentral_applied_group_policy_profile").FromTable("site_configurations");
        Delete.Column("meshcentral_group_policy_profile").FromTable("site_configurations");

        Delete.Column("meshcentral_group_policy_profile").FromTable("client_configurations");
        Delete.Column("meshcentral_group_policy_profile").FromTable("server_configurations");
    }
}
