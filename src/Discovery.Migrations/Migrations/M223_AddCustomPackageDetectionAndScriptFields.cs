using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Detecção de instalação e tipo de comando (comando único / script CMD / script
/// PowerShell) dos pacotes custom da Loja de Aplicativos.
///
/// - <c>install_command_kind</c>/<c>uninstall_command_kind</c>: AppCommandKind (default 0 =
///   comando único via cmd /C), retrocompatível com os registros existentes.
/// - <c>detection_mode</c>: AppDetectionMode (default 0 = sem detecção).
/// - <c>inventory_software_id</c>: softwareId (Guid) do catálogo de inventário, SEM FK
///   (excluir o app do inventário não pode travar a loja). <c>detection_name</c>/
///   <c>detection_publisher</c> são o snapshot usado pelo agent para casar com o
///   inventário LOCAL, já que o Guid é atribuído pelo servidor.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261213_223)]
public class M223_AddCustomPackageDetectionAndScriptFields : Migration
{
    private const string Table = "app_packages";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("install_command_kind").Exists())
            Alter.Table(Table).AddColumn("install_command_kind").AsInt32().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(Table).Column("uninstall_command_kind").Exists())
            Alter.Table(Table).AddColumn("uninstall_command_kind").AsInt32().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(Table).Column("detection_mode").Exists())
            Alter.Table(Table).AddColumn("detection_mode").AsInt32().NotNullable().WithDefaultValue(0);

        if (!Schema.Table(Table).Column("inventory_software_id").Exists())
            Alter.Table(Table).AddColumn("inventory_software_id").AsString(64).Nullable();

        if (!Schema.Table(Table).Column("detection_name").Exists())
            Alter.Table(Table).AddColumn("detection_name").AsString(500).Nullable();

        if (!Schema.Table(Table).Column("detection_publisher").Exists())
            Alter.Table(Table).AddColumn("detection_publisher").AsString(500).Nullable();

        if (!Schema.Table(Table).Column("detection_min_version").Exists())
            Alter.Table(Table).AddColumn("detection_min_version").AsString(100).Nullable();

        if (!Schema.Table(Table).Column("detection_command").Exists())
            Alter.Table(Table).AddColumn("detection_command").AsString(2000).Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // Guardas de existência: o Down não pode falhar se a coluna não existir
        // (banco onde o Up não rodou ou rodou parcialmente).
        if (Schema.Table(Table).Column("install_command_kind").Exists())
            Delete.Column("install_command_kind").FromTable(Table);

        if (Schema.Table(Table).Column("uninstall_command_kind").Exists())
            Delete.Column("uninstall_command_kind").FromTable(Table);

        if (Schema.Table(Table).Column("detection_mode").Exists())
            Delete.Column("detection_mode").FromTable(Table);

        if (Schema.Table(Table).Column("inventory_software_id").Exists())
            Delete.Column("inventory_software_id").FromTable(Table);

        if (Schema.Table(Table).Column("detection_name").Exists())
            Delete.Column("detection_name").FromTable(Table);

        if (Schema.Table(Table).Column("detection_publisher").Exists())
            Delete.Column("detection_publisher").FromTable(Table);

        if (Schema.Table(Table).Column("detection_min_version").Exists())
            Delete.Column("detection_min_version").FromTable(Table);

        if (Schema.Table(Table).Column("detection_command").Exists())
            Delete.Column("detection_command").FromTable(Table);
    }
}
