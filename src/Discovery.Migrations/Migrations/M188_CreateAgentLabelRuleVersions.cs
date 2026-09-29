using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Historico de versoes das regras de auto-labeling.
///
/// O historico por agente (agent_label_change_logs) mostra o efeito das regras; esta
/// tabela guarda a CONFIGURACAO a cada escrita, para auditoria e rollback manual.
/// Aditivo: apenas uma tabela nova.
/// </summary>
[Migration(20261021_188)]
public class M188_CreateAgentLabelRuleVersions : Migration
{
    private const string Table = "agent_label_rule_versions";

    public override void Up()
    {
        if (Schema.Table(Table).Exists())
            return;

        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("rule_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable()
            .WithColumn("apply_mode").AsInt32().NotNullable()
            .WithColumn("expression_json").AsCustom("jsonb").NotNullable()
            .WithColumn("changed_by").AsString(256).Nullable()
            .WithColumn("changed_at").AsDateTime().NotNullable();

        Create.Index("ix_agent_label_rule_versions_rule_changed")
            .OnTable(Table)
            .OnColumn("rule_id").Ascending()
            .OnColumn("changed_at").Descending();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index("ix_agent_label_rule_versions_rule_changed").Exists())
            Delete.Index("ix_agent_label_rule_versions_rule_changed").OnTable(Table);

        Delete.Table(Table);
    }
}
