using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(80)]
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
