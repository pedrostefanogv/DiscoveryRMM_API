using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_080)]
public class M080_RemoveMeshRightsOverrideFromUserGroupRoles : Migration
{
    public override void Up()
    {
        Delete.Column("mesh_rights_override").FromTable("user_group_roles");
    }

    public override void Down()
    {
        Alter.Table("user_group_roles")
            .AddColumn("mesh_rights_override").AsInt32().Nullable();
    }
}

[Migration(20260325_080)]
public class M080_AddNatsServerUrl : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_url").AsCustom("text").NotNullable().WithDefaultValue("nats://localhost:4222");
    }

    public override void Down()
    {
        Delete.Column("nats_server_url").FromTable("server_configurations");
    }
}
