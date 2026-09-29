using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona o modo de notificacao ao usuario nas automation tasks:
///
/// - notification_mode: Silent (0), Prompt (1, Welcome do PSADT com
///   Continuar/Adiar) ou Toast (2, aviso informativo sem interacao).
/// - toast_timing: Before (0) ou After (1) para o toast informativo.
///
/// Backfill: tarefas antigas com requires_approval = true viram Prompt; as
/// demais viram Silent (a lista ja as exibia como "Silenciosa"). A coluna
/// requires_approval permanece e passa a ser derivada de notification_mode.
/// </summary>
[Migration(20261014_186)]
public class M186_AddNotificationModeToAutomationTasks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (!Schema.Table("automation_task_definitions").Column("notification_mode").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("notification_mode").AsInt32().NotNullable().WithDefaultValue(0);

            // Tarefas que ja pediam o Welcome continuam com o prompt.
            Execute.Sql(@"
                UPDATE automation_task_definitions
                SET notification_mode = 1
                WHERE requires_approval = true;");
        }

        if (!Schema.Table("automation_task_definitions").Column("toast_timing").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("toast_timing").AsInt32().NotNullable().WithDefaultValue(1);
        }
    }

    public override void Down()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (Schema.Table("automation_task_definitions").Column("toast_timing").Exists())
            Delete.Column("toast_timing").FromTable("automation_task_definitions");

        if (Schema.Table("automation_task_definitions").Column("notification_mode").Exists())
            Delete.Column("notification_mode").FromTable("automation_task_definitions");
    }
}
