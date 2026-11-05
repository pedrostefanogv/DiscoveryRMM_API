using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Vínculo do SENSOR com o auto-ticket:
///  - auto_ticket_policy: 0 = Never, 1 = OnEscalation (default), 2 = Always
///  - auto_ticket_min_duration_seconds: 0 = herda da política hierárquica
///  - auto_ticket_impact: 0 = Low, 1 = Medium (default), 2 = High, 3 = Critical
///
/// O default OnEscalation preserva o comportamento anterior (só abre chamado na
/// abertura/escala), agora de forma explícita e configurável por sensor.
///
/// Os valores são literais de propósito: o projeto Discovery.Migrations não
/// referencia Discovery.Core (só FluentMigrator), então os enums não estão
/// disponíveis aqui. Mantenha em sincronia com Discovery.Core.Enums.
/// </summary>
[Migration(20270103_268)]
public class M268_AddSensorAutoTicketColumns : Migration
{
    private const string Table = "sensor_definitions";

    private const int PolicyOnEscalation = 1;
    private const int ImpactMedium = 1;

    public override void Up()
    {
        if (!Schema.Table(Table).Column("auto_ticket_policy").Exists())
        {
            Alter.Table(Table)
                .AddColumn("auto_ticket_policy")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(PolicyOnEscalation);
        }

        if (!Schema.Table(Table).Column("auto_ticket_min_duration_seconds").Exists())
        {
            Alter.Table(Table)
                .AddColumn("auto_ticket_min_duration_seconds")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(0);
        }

        if (!Schema.Table(Table).Column("auto_ticket_impact").Exists())
        {
            Alter.Table(Table)
                .AddColumn("auto_ticket_impact")
                .AsInt32()
                .NotNullable()
                .WithDefaultValue(ImpactMedium);
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column("auto_ticket_impact").Exists())
            Delete.Column("auto_ticket_impact").FromTable(Table);

        if (Schema.Table(Table).Column("auto_ticket_min_duration_seconds").Exists())
            Delete.Column("auto_ticket_min_duration_seconds").FromTable(Table);

        if (Schema.Table(Table).Column("auto_ticket_policy").Exists())
            Delete.Column("auto_ticket_policy").FromTable(Table);
    }
}
