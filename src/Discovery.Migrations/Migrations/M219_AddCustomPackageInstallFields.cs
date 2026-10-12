using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Campos de instalacao dos pacotes custom (cadastro manual na Loja de Softwares):
/// comando de desinstalacao, sinalizacao de ZIP a extrair no host, nome original
/// do instalador e a chave do icone/capa no object storage. O upload do arquivo em
/// si usa as colunas file_* ja existentes desde a M054.
///
/// NOTA: esta migração nasceu como M160 e colidia com M160_CreateAgentLabelSuppressions
/// (mesma versão 20260923_160) — duas migrações na mesma versão quebram a ordem do
/// MigrateUp em instalações novas. Renumerada para 20261206_219 (próxima livre),
/// mantendo o Up idempotente para não falhar onde as colunas já existirem.
/// </summary>
[Migration(20261206_219)]
public class M219_AddCustomPackageInstallFields : Migration
{
    private const string Table = "app_packages";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("uninstall_command").Exists())
            Alter.Table(Table).AddColumn("uninstall_command").AsString(1000).Nullable();

        if (!Schema.Table(Table).Column("extract_zip").Exists())
            Alter.Table(Table).AddColumn("extract_zip").AsBoolean().NotNullable().WithDefaultValue(false);

        if (!Schema.Table(Table).Column("file_name").Exists())
            Alter.Table(Table).AddColumn("file_name").AsString(400).Nullable();

        if (!Schema.Table(Table).Column("icon_object_key").Exists())
            Alter.Table(Table).AddColumn("icon_object_key").AsString(1000).Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // Guardas de existência: o Down não pode falhar se a coluna não existir
        // (banco onde o Up não rodou ou rodou parcialmente).
        if (Schema.Table(Table).Column("uninstall_command").Exists())
            Delete.Column("uninstall_command").FromTable(Table);

        if (Schema.Table(Table).Column("extract_zip").Exists())
            Delete.Column("extract_zip").FromTable(Table);

        if (Schema.Table(Table).Column("file_name").Exists())
            Delete.Column("file_name").FromTable(Table);

        if (Schema.Table(Table).Column("icon_object_key").Exists())
            Delete.Column("icon_object_key").FromTable(Table);
    }
}
