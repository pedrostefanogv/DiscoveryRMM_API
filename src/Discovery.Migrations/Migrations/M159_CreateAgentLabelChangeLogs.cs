using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Historico (auditoria) de mudancas de labels de agentes. Antes nao havia rastro:
/// ao remover uma label automatica, ela simplesmente desaparecia sem registro de
/// quando, por que, ou por qual motivo de avaliacao.
/// </summary>
[Migration(20260923_159)]
public class M159_CreateAgentLabelChangeLogs : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_label_change_logs").Exists())
            return;

        Create.Table("agent_label_change_logs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
                .ForeignKey("agents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("label").AsString(120).NotNullable()
            .WithColumn("source_type").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("action").AsString(20).NotNullable()
            .WithColumn("reason").AsString(256).Nullable()
            .WithColumn("actor").AsString(256).Nullable()
            .WithColumn("occurred_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agent_label_change_logs_agent_occurred")
            .OnTable("agent_label_change_logs")
            .OnColumn("agent_id").Ascending()
            .OnColumn("occurred_at").Descending();

        Create.Index("ix_agent_label_change_logs_occurred")
            .OnTable("agent_label_change_logs")
            .OnColumn("occurred_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("agent_label_change_logs");
    }
}
