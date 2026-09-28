using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Transforma "requires_approval" em notificacao ao usuario (Welcome do PSADT,
/// Continuar/Adiar) e adiciona as opcoes de adiamento, fechamento de processos e
/// tempo para a acao padrao de continuar quando o usuario nao responde.
///
/// - allow_defer: usuario pode adiar (botao Adiar do Welcome).
/// - close_processes_json: nomes de processos fechados antes de executar.
/// - user_prompt_timeout_seconds: timeout da acao padrao (default 60s).
///
/// O valor de requires_approval permanece intacto: ele agora significa
/// "notificar usuario" e nao mais "exige aprovacao".
/// </summary>
[Migration(20261012_184)]
public class M184_AddUserPromptToAutomationTasks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (!Schema.Table("automation_task_definitions").Column("allow_defer").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("allow_defer").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (!Schema.Table("automation_task_definitions").Column("close_processes_json").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("close_processes_json").AsCustom("jsonb").Nullable();
        }

        if (!Schema.Table("automation_task_definitions").Column("user_prompt_timeout_seconds").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("user_prompt_timeout_seconds").AsInt32().NotNullable().WithDefaultValue(60);
        }
    }

    public override void Down()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (Schema.Table("automation_task_definitions").Column("user_prompt_timeout_seconds").Exists())
            Delete.Column("user_prompt_timeout_seconds").FromTable("automation_task_definitions");

        if (Schema.Table("automation_task_definitions").Column("close_processes_json").Exists())
            Delete.Column("close_processes_json").FromTable("automation_task_definitions");

        if (Schema.Table("automation_task_definitions").Column("allow_defer").Exists())
            Delete.Column("allow_defer").FromTable("automation_task_definitions");
    }
}
