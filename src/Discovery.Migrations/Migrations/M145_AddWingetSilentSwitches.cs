using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona colunas de switches silenciosos ao catálogo Winget.
/// silent_command: switches para instalação silenciosa (ex.: "/S /PreventRebootRequired=true").
/// silent_with_progress_command: fallback silencioso com progresso.
/// </summary>
[Migration(20260830_145)]
public class M145_AddWingetSilentSwitches : Migration
{
    public override void Up()
    {
        // A tabela winget_packages pode não existir em servidores que usam apenas
        // o catálogo unificado (app_packages) — M055_RemoveLegacyAppStoreTables a removeu.
        // Nesses casos não há nada a fazer: os switches silenciosos vivem no MetadataJson.
        if (!Schema.Table("winget_packages").Exists())
            return;

        if (!Schema.Table("winget_packages").Column("silent_command").Exists())
        {
            Alter.Table("winget_packages")
                .AddColumn("silent_command").AsString(1000).NotNullable().WithDefaultValue("");
        }

        if (!Schema.Table("winget_packages").Column("silent_with_progress_command").Exists())
        {
            Alter.Table("winget_packages")
                .AddColumn("silent_with_progress_command").AsString(1000).NotNullable().WithDefaultValue("");
        }
    }

    public override void Down()
    {
        if (!Schema.Table("winget_packages").Exists())
            return;

        if (Schema.Table("winget_packages").Column("silent_with_progress_command").Exists())
        {
            Delete.Column("silent_with_progress_command").FromTable("winget_packages");
        }

        if (Schema.Table("winget_packages").Column("silent_command").Exists())
        {
            Delete.Column("silent_command").FromTable("winget_packages");
        }
    }
}
