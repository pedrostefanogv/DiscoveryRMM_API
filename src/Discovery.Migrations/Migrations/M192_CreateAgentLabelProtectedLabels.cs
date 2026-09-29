using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Labels protegidas: nenhuma regra no modo Remover pode apaga-las.
/// Aditivo: apenas uma tabela nova com indice unico por label.
/// </summary>
[Migration(20261025_192)]
public class M192_CreateAgentLabelProtectedLabels : Migration
{
    private const string Table = "agent_label_protected_labels";

    public override void Up()
    {
        if (Schema.Table(Table).Exists())
            return;

        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable();

        Create.Index("ux_agent_label_protected_labels_label")
            .OnTable(Table)
            .OnColumn("label").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index("ux_agent_label_protected_labels_label").Exists())
            Delete.Index("ux_agent_label_protected_labels_label").OnTable(Table);

        Delete.Table(Table);
    }
}
