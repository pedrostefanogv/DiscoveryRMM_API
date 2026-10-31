using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Unicidade da chave da tarefa de automação por ESCOPO (não global).
///
/// Antes: \`ux_automation_task_definitions_key\` era UNIQUE(key) global e contava
/// tarefas soft-deleted. Efeitos (incidente/atendimento 2026-10-31):
///  - criar a mesma policy ("Instalar Chrome") para outro cliente/site devolvia
///    400 "Já existe uma tarefa com esta chave";
///  - uma tarefa excluída bloqueava a chave para sempre.
///
/// Agora: UNIQUE (escopo, key) apenas para tarefas ATIVAS
/// (\`deleted_at IS NULL\`). O escopo é \`coalesce(client_id, site_id, agent_id)\`;
/// tarefas globais (todos nulos) compartilham o UUID sentinela, portanto seguem
/// únicas globalmente entre si. Mesmo critério de \`ExistsKeyAsync\`.
/// </summary>
[Migration(20261231_254)]
public class M254_AutomationTaskKeyPerScope : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        Execute.Sql("DROP INDEX IF EXISTS ux_automation_task_definitions_key");
        // Índice parcial idempotente: deleta/reconstrói para garantir o critério novo.
        Execute.Sql(@"DROP INDEX IF EXISTS ux_automation_task_definitions_scope_key");
        Execute.Sql(@"CREATE UNIQUE INDEX ux_automation_task_definitions_scope_key
                      ON automation_task_definitions
                         (coalesce(client_id, site_id, agent_id, '00000000-0000-0000-0000-000000000000'::uuid), key)
                      WHERE key IS NOT NULL AND deleted_at IS NULL");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        Execute.Sql("DROP INDEX IF EXISTS ux_automation_task_definitions_scope_key");
        Execute.Sql(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_automation_task_definitions_key
                      ON automation_task_definitions (key) WHERE key IS NOT NULL");
    }
}
