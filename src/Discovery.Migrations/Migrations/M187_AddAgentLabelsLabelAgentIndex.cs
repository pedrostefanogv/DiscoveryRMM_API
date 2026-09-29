using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Indice label-first em agent_labels.
///
/// As consultas do processo de labels filtram por label: agents-by-label (cursor por
/// agent_id), usage (agrupamento por label) e distinct. O unico indice existente era
/// ux_agent_labels_agent_label (agent_id, label), que nao atende predicado em label.
/// Aditivo: apenas um indice novo.
/// </summary>
[Migration(20261020_187)]
public class M187_AddAgentLabelsLabelAgentIndex : Migration
{
    private const string Table = "agent_labels";
    private const string IndexName = "ix_agent_labels_label_agent";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists()) return;

        if (!Schema.Table(Table).Index(IndexName).Exists())
        {
            Create.Index(IndexName)
                .OnTable(Table)
                .OnColumn("label").Ascending()
                .OnColumn("agent_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Exists() && Schema.Table(Table).Index(IndexName).Exists())
        {
            Delete.Index(IndexName).OnTable(Table);
        }
    }
}
