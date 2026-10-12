using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Chave padronizada da tarefa (mesmo conceito de chave dos campos customizados):
/// identificador normalizado [a-z0-9_-] derivado do nome, usado por relatórios,
/// automações e referências externas. Nullable para não quebrar tarefas existentes.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261205_218)]
public class M218_AddKeyToAutomationTasks : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("key").Exists())
        {
            Alter.Table(Table).AddColumn("key").AsString(100).Nullable();
            Execute.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_automation_task_definitions_key
                          ON automation_task_definitions (key) WHERE key IS NOT NULL");
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        Execute.Sql("DROP INDEX IF EXISTS ux_automation_task_definitions_key");
        if (Schema.Table(Table).Column("key").Exists())
            Delete.Column("key").FromTable(Table);
    }
}
