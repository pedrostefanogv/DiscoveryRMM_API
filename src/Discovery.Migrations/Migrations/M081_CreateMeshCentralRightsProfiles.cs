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

        // Seeds idempotentes com GUID gerado pelo banco (name é unique via ix_meshcentral_rights_profiles_name)
        Execute.Sql(@"
            INSERT INTO meshcentral_rights_profiles (id, name, description, rights_mask, is_system, created_at, updated_at)
            SELECT gen_random_uuid(), 'viewer',   'Visualizacao limitada',      8448,  true, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM meshcentral_rights_profiles WHERE name = 'viewer');

            INSERT INTO meshcentral_rights_profiles (id, name, description, rights_mask, is_system, created_at, updated_at)
            SELECT gen_random_uuid(), 'operator', 'Operacao remota padrao',    61176, true, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM meshcentral_rights_profiles WHERE name = 'operator');

            INSERT INTO meshcentral_rights_profiles (id, name, description, rights_mask, is_system, created_at, updated_at)
            SELECT gen_random_uuid(), 'admin',    'Administracao completa',       -1, true, NOW(), NOW()
            WHERE NOT EXISTS (SELECT 1 FROM meshcentral_rights_profiles WHERE name = 'admin');
        ");
    }

    public override void Down()
    {
        Delete.Table("meshcentral_rights_profiles");
    }
}

[Migration(20260325_081)]
public class M081_AddNatsServerUrlInternalExternal : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_host_internal").AsCustom("text").NotNullable().WithDefaultValue("localhost")
            .AddColumn("nats_server_host_external").AsCustom("text").NotNullable().WithDefaultValue("localhost")
            .AddColumn("nats_use_wss_external").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("nats_use_wss_external").FromTable("server_configurations");
        Delete.Column("nats_server_host_external").FromTable("server_configurations");
        Delete.Column("nats_server_host_internal").FromTable("server_configurations");
    }
}
