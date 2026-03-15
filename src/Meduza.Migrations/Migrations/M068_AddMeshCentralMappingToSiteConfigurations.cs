using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260315_068)]
public class M068_AddMeshCentralMappingToSiteConfigurations : Migration
{
    public override void Up()
    {
        if (!Schema.Table("site_configurations").Column("meshcentral_group_name").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("meshcentral_group_name").AsString(200).Nullable();
        }

        if (!Schema.Table("site_configurations").Column("meshcentral_mesh_id").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("meshcentral_mesh_id").AsString(200).Nullable();
        }

        if (!Schema.Table("site_configurations").Index("ix_site_configurations_meshcentral_mesh_id").Exists())
        {
            Create.Index("ix_site_configurations_meshcentral_mesh_id")
                .OnTable("site_configurations")
                .OnColumn("meshcentral_mesh_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Index("ix_site_configurations_meshcentral_mesh_id").Exists())
        {
            Delete.Index("ix_site_configurations_meshcentral_mesh_id").OnTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("meshcentral_mesh_id").Exists())
        {
            Delete.Column("meshcentral_mesh_id").FromTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("meshcentral_group_name").Exists())
        {
            Delete.Column("meshcentral_group_name").FromTable("site_configurations");
        }
    }
}
