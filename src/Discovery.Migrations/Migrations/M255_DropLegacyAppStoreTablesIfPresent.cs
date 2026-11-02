using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Limpeza definitiva das tabelas legadas de catálogo (winget_packages /
/// chocolatey_packages), que a M055 removeu em 2026-03.
///
/// Motivo (incidente 2026-10-31): a M055 já está registrada como aplicada, então o
/// Up dela nunca roda de novo — mas ambientes que passaram por rollback/restore
/// ficaram com as tabelas recriadas pelo antigo M055.Down (schema incompleto, dono
/// postgres, SEM GRANT para o role da API). O EF ainda as consultava na criação de
/// tarefa de pacote → 42501 "permission denied" → 25P02 → HTTP 500 em
/// POST /api/v1/automation/tasks.
///
/// Esta migration é idempotente e roda em qualquer ambiente, garantindo o estado
/// final (tabelas ausentes). O catálogo oficial é `app_packages` (M250).
/// </summary>
[Migration(20261231_255)]
public class M255_DropLegacyAppStoreTablesIfPresent : Migration
{
    public override void Up()
    {
        // DROP TABLE IF EXISTS: não depende de schema.Exists() nem do VersionInfo —
        // funciona tanto para a tabela ausente quanto para a recriada por rollback.
        Execute.Sql("DROP TABLE IF EXISTS winget_packages;");
        Execute.Sql("DROP TABLE IF EXISTS chocolatey_packages;");
    }

    /// <summary>
    /// Não recria as tabelas (mesmo motivo do M055.Down no-op): ressuscitá-las sem
    /// colunas/grants posteriores reintroduz o drift que causou o incidente.
    /// Reverter exige restore de backup anterior à M055.
    /// </summary>
    public override void Down()
    {
        // Intencionalmente vazio.
    }
}
