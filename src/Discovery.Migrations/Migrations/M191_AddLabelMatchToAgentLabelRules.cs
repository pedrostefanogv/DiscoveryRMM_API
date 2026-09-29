using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Alvo do modo Remover nas regras de auto-labeling (exato/prefixo/regex).
///
/// Aditivo: regras existentes ficam com o default 0 (Exact) e o comportamento atual
/// nao muda. A coluna so tem efeito quando apply_mode = 3 (Remove).
/// </summary>
[Migration(20261024_191)]
public class M191_AddLabelMatchToAgentLabelRules : Migration
{
    private const string RulesTable = "agent_label_rules";
    private const string VersionsTable = "agent_label_rule_versions";
    private const string Column = "label_match";

    public override void Up()
    {
        AddColumnIfMissing(RulesTable);
        AddColumnIfMissing(VersionsTable);
    }

    public override void Down()
    {
        DropColumnIfExists(RulesTable);
        DropColumnIfExists(VersionsTable);
    }

    private void AddColumnIfMissing(string table)
    {
        if (!Schema.Table(table).Exists())
            return;

        if (Schema.Table(table).Column(Column).Exists())
            return;

        Alter.Table(table)
            .AddColumn(Column).AsInt32().NotNullable().WithDefaultValue(0);
    }

    private void DropColumnIfExists(string table)
    {
        if (Schema.Table(table).Exists() && Schema.Table(table).Column(Column).Exists())
            Delete.Column(Column).FromTable(table);
    }
}
