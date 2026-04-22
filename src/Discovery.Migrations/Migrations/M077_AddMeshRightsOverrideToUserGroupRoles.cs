using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_077)]
public class M077_AddMeshRightsOverrideToUserGroupRoles : Migration
{
    public override void Up()
    {
        Alter.Table("user_group_roles")
            .AddColumn("mesh_rights_override").AsInt32().Nullable();
    }

    public override void Down()
    {
        Delete.Column("mesh_rights_override").FromTable("user_group_roles");
    }
}
