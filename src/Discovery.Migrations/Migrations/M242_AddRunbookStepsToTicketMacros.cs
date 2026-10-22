using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Runbooks executáveis no chamado (escopo do agente B).
/// ticket_macros recebe runbook_steps_json JSONB NULL: lista ordenada de
/// passos { order, automationScriptId, argsJson?, evidenceLabel? }.
/// null = macro comum (resposta rápida), sem runbook.
/// Idempotente (checagem de Schema) no padrão das M233/M235.
/// </summary>
[Migration(20261222_242)]
public class M242_AddRunbookStepsToTicketMacros : Migration
{
    private const string Table = "ticket_macros";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("runbook_steps_json").Exists())
            Alter.Table(Table).AddColumn("runbook_steps_json").AsCustom("jsonb").Nullable();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("runbook_steps_json").Exists())
            Delete.Column("runbook_steps_json").FromTable(Table);
    }
}
