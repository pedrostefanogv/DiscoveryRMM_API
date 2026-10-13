using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Ordem de execução da tarefa de automação (0–99). O agent executa as tarefas
/// da policy em ordem crescente e, no empate, roda primeiro a tarefa atualizada
/// há mais tempo.
///
/// Backfill por tipo de ação (mesmo default do domínio, ver
/// AutomationTaskExecutionOrder): instalar 10, instalar/atualizar e atualizar 20,
/// remover 30, script 40, comando personalizado 50.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261212_222)]
public class M222_AddExecutionOrderToAutomationTasks : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("execution_order").Exists())
        {
            Alter.Table(Table)
                .AddColumn("execution_order")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(50);

            // action_type: 0 InstallPackage, 1 UpdatePackage, 2 RunScript,
            //              3 CustomCommand, 4 RemovePackage, 5 UpdateOrInstallPackage
            Execute.Sql(@"UPDATE automation_task_definitions
                          SET execution_order = CASE action_type
                              WHEN 0 THEN 10
                              WHEN 1 THEN 20
                              WHEN 5 THEN 20
                              WHEN 4 THEN 30
                              WHEN 2 THEN 40
                              WHEN 3 THEN 50
                              ELSE 50
                          END
                          WHERE execution_order = 50");
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("execution_order").Exists())
            Delete.Column("execution_order").FromTable(Table);
    }
}
