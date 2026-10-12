using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Tarefa coletora: coluna que aponta para o campo customizado de escopo Agente
/// que recebe a saída da execução (última linha não vazia do stdout).
/// NULL = tarefa comum (comportamento atual).
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261203_216)]
public class M216_AddCollectorFieldToAutomationTasks : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("collector_field_definition_id").Exists())
        {
            Alter.Table(Table).AddColumn("collector_field_definition_id").AsGuid().Nullable();
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("collector_field_definition_id").Exists())
            Delete.Column("collector_field_definition_id").FromTable(Table);
    }
}
