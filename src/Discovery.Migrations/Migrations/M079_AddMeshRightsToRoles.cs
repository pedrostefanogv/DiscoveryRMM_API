using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_079)]
public class M079_AddMeshRightsToRoles : Migration
{
    public override void Up()
    {
        Alter.Table("roles")
            .AddColumn("mesh_rights_mask").AsInt32().Nullable()
            .AddColumn("mesh_rights_profile").AsString(64).Nullable();
    }

    public override void Down()
    {
        Delete.Column("mesh_rights_profile").FromTable("roles");
        Delete.Column("mesh_rights_mask").FromTable("roles");
    }
}
