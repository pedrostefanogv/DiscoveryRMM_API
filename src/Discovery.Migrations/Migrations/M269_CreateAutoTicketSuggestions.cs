using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Livro auditável das sugestões da IA no auto-ticket.
///
/// Espelha ticket_assignment_decisions (que já serve à triagem de atribuição):
/// guarda confiança, se foi aplicada, por que não foi, e quando um humano a
/// desfez — permitindo medir a taxa de override antes de dar mais autonomia.
///
/// Guardamos também as NÃO aplicadas: sem elas não há como avaliar se a IA
/// acerta, que é o insumo para decidir se vale ir de Suggest para AutoApply.
/// </summary>
[Migration(20270104_269)]
public class M269_CreateAutoTicketSuggestions : Migration
{
    private const string Table = "auto_ticket_suggestions";

    public override void Up()
    {
        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("rule_execution_id").AsGuid().Nullable()
            .WithColumn("monitoring_event_id").AsGuid().NotNullable()
            .WithColumn("ticket_id").AsGuid().Nullable()
            .WithColumn("agent_id").AsGuid().Nullable()
            .WithColumn("field").AsInt32().NotNullable()
            .WithColumn("suggested_value").AsString(500).Nullable()
            .WithColumn("applied_value").AsString(500).Nullable()
            .WithColumn("confidence").AsDouble().NotNullable().WithDefaultValue(0)
            .WithColumn("mode").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("strategy_source").AsString(50).NotNullable().WithDefaultValue("ai")
            .WithColumn("rationale").AsString(1000).Nullable()
            .WithColumn("model").AsString(200).Nullable()
            .WithColumn("tokens_used").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("applied").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("not_applied_reason").AsString(500).Nullable()
            .WithColumn("overridden_at").AsDateTimeOffset().Nullable()
            .WithColumn("overridden_by_user_id").AsGuid().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_auto_ticket_suggestions_event_id")
            .OnTable(Table)
            .OnColumn("monitoring_event_id").Ascending();

        // Índice para medir taxa de aplicação/override por campo ao longo do tempo.
        Create.Index("ix_auto_ticket_suggestions_field_created")
            .OnTable(Table)
            .OnColumn("field").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Table(Table);
    }
}
