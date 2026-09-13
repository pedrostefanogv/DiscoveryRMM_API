using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona app_catalog_sync_settings_json em server_configurations: persiste o
/// resultado da última sincronização de catálogo (Winget/Chocolatey) por tipo,
/// para que o console web reflita também as execuções automáticas (Quartz) —
/// antes, o status só existia em memória e era alimentado apenas pelo sync manual.
/// </summary>
[Migration(20260913_147)]
public class M147_AddAppCatalogSyncSettingsToServerConfiguration : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("app_catalog_sync_settings_json").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("app_catalog_sync_settings_json").AsCustom("text").NotNullable().WithDefaultValue("{}");
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Column("app_catalog_sync_settings_json").Exists())
        {
            Delete.Column("app_catalog_sync_settings_json").FromTable("server_configurations");
        }
    }
}
