using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Amplia o texto dos comandos do pacote custom: <c>install_command</c> e
/// <c>uninstall_command</c> passam de varchar(1000) para <c>text</c>.
///
/// Motivo: quando o tipo do comando é script (PowerShell/CMD) o campo guarda o
/// SCRIPT INTEIRO — um instalador real (ex.: Foxit PDF Editor) passa de 9 mil
/// caracteres e era recusado/truncado no editor e na API. O teto passa a ser de
/// aplicação (AppStoreService.MaxCustomCommandLength), não do banco.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261217_227)]
public class M227_ExpandCustomPackageCommandColumns : Migration
{
    private const string Table = "app_packages";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("install_command").Exists())
            Alter.Table(Table).AlterColumn("install_command").AsString(int.MaxValue).Nullable();

        if (Schema.Table(Table).Column("uninstall_command").Exists())
            Alter.Table(Table).AlterColumn("uninstall_command").AsString(int.MaxValue).Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // Guardas de existência: o Down não pode falhar se a coluna não existir
        // (banco onde o Up não rodou ou rodou parcialmente).
        if (Schema.Table(Table).Column("install_command").Exists())
            Alter.Table(Table).AlterColumn("install_command").AsString(1000).Nullable();

        if (Schema.Table(Table).Column("uninstall_command").Exists())
            Alter.Table(Table).AlterColumn("uninstall_command").AsString(1000).Nullable();
    }
}
