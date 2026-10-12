using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Opção da tarefa: gravar a saída da execução como uma NOTA no agent.
/// Serve para coleta/relatório mais amplo (o texto fica no histórico do agente).
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261204_217)]
public class M217_AddWriteOutputToNoteToAutomationTasks : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("write_output_to_note").Exists())
        {
            Alter.Table(Table).AddColumn("write_output_to_note").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("write_output_to_note").Exists())
            Delete.Column("write_output_to_note").FromTable(Table);
    }
}
