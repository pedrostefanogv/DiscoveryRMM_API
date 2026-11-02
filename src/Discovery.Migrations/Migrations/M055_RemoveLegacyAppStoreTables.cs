using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_055)]
public class M055_RemoveLegacyAppStoreTables : Migration
{
    public override void Up()
    {
        if (Schema.Table("winget_packages").Exists())
            Delete.Table("winget_packages");

        if (Schema.Table("chocolatey_packages").Exists())
            Delete.Table("chocolatey_packages");
    }

    /// <summary>
    /// Histórico: o Down() original RECRIAVA as duas tabelas com o schema de M049/M048
    /// (sem as colunas adicionadas depois, ex. silent_command da M145), sem GRANT para
    /// o role da API e sem nenhum dado. Um rollback deixava o ambiente com drift
    /// permanente — foi a origem provável das tabelas órfãs que causaram o incidente
    /// de 2026-10-31 (42501 permission denied → 25P02 → HTTP 500 no POST de
    /// automation task).
    ///
    /// Como o catálogo oficial agora é `app_packages` (M250) e o mapeamento EF das
    /// tabelas legadas foi removido, um rollback não deve ressuscitá-las: Down é no-op.
    /// Para reverter de verdade, restaure um backup anterior à M055.
    /// </summary>
    public override void Down()
    {
        // Intencionalmente vazio — ver comentário acima.
    }
}
