using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Supressoes de labels automaticas removidas manualmente. Sem essa tabela, o
/// reconcile recriava a label logo apos o usuario remove-la, porque o match da
/// regra continuava existindo.
/// </summary>
[Migration(20260923_160)]
public class M160_CreateAgentLabelSuppressions : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_label_suppressions").Exists())
            return;

        Create.Table("agent_label_suppressions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
                .ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("suppressed_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("suppressed_by").AsString(256).Nullable();

        Create.Index("ux_agent_label_suppressions_agent_label")
            .OnTable("agent_label_suppressions")
            .OnColumn("agent_id").Ascending()
            .OnColumn("label").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("agent_label_suppressions");
    }
}
