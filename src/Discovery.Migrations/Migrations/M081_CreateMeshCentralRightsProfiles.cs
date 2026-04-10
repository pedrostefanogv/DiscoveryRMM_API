using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_081)]
public class M081_CreateMeshCentralRightsProfiles : Migration
{
    public override void Up()
    {
        Create.Table("meshcentral_rights_profiles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(64).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("rights_mask").AsInt32().NotNullable()
            .WithColumn("is_system").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_meshcentral_rights_profiles_name")
            .OnTable("meshcentral_rights_profiles")
            .OnColumn("name").Ascending()
            .WithOptions().Unique();

        Insert.IntoTable("meshcentral_rights_profiles").Row(new
        {
            id = new Guid("35F20A2A-1C48-4A91-9CC2-4D47D50398A6"),
            name = "viewer",
            description = "Visualizacao limitada",
            rights_mask = 8448,
            is_system = true,
            created_at = SystemMethods.CurrentUTCDateTime,
            updated_at = SystemMethods.CurrentUTCDateTime
        });

        Insert.IntoTable("meshcentral_rights_profiles").Row(new
        {
            id = new Guid("D8143E8A-A280-44E2-A878-24F5B4F29A81"),
            name = "operator",
            description = "Operacao remota padrao",
            rights_mask = 61176,
            is_system = true,
            created_at = SystemMethods.CurrentUTCDateTime,
            updated_at = SystemMethods.CurrentUTCDateTime
        });

        Insert.IntoTable("meshcentral_rights_profiles").Row(new
        {
            id = new Guid("B2CE539F-0EA8-4BC0-A845-6189A8A4BF0D"),
            name = "admin",
            description = "Administracao completa",
            rights_mask = -1,
            is_system = true,
            created_at = SystemMethods.CurrentUTCDateTime,
            updated_at = SystemMethods.CurrentUTCDateTime
        });
    }

    public override void Down()
    {
        Delete.Table("meshcentral_rights_profiles");
    }
}
