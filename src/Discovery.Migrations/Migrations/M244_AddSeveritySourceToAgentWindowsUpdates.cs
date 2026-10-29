using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Proveniência da severidade do update (decisão D10 — derivar no agente).
///
/// O agente passou a enviar `severitySource` junto de `severity`:
///  - `vendor`: rating publicado pela Microsoft (IUpdate.MsrcSeverity);
///  - `derived`: rating inferido pelo agente (classificação/título), com teto
///    `Important` — nunca `Critical`, porque só o MSRC pode dizer critical;
///  - `unknown`: sem rating e sem derivação aplicável (drivers, definitions,
///    feature packs, upgrades, tools).
///
/// Por que coluna: a UI precisa rotular "derivada" e o suporte precisa saber se
/// uma regra de auto-aprovação por severidade casou por rating oficial ou por
/// inferência. Sem isso, "Important" derivado era indistinguível do oficial.
///
/// Compatibilidade: coluna NULL-able; agente antigo simplesmente não envia e o
/// valor fica null (nunca "unknown" inventado). Idempotente (checagem de
/// Schema) no padrão das M238/M239.
/// </summary>
[Migration(20261223_244)]
public class M244_AddSeveritySourceToAgentWindowsUpdates : Migration
{
    private const string Table = "agent_windows_updates";
    private const string Column = "severity_source";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {Column} varchar(20) NULL");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
