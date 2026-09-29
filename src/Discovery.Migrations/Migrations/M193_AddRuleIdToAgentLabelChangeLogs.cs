using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Atribui a REGRA a cada mudanca de label registrada.
///
/// Sem isso o historico mostra apenas "Aplicada/Removida &lt;label&gt;", sem dizer qual
/// regra causou — impossivel explicar oscilacoes (aplicar/remover em sequencia).
/// Aditivo: coluna anulavel em registros antigos.
/// </summary>
[Migration(20261026_193)]
public class M193_AddRuleIdToAgentLabelChangeLogs : Migration
{
    private const string Table = "agent_label_change_logs";
    private const string Column = "rule_id";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Alter.Table(Table)
            .AddColumn(Column).AsGuid().Nullable();
    }

    public override void Down()
    {
        if (Schema.Table(Table).Exists() && Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
