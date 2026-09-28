using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Índices por `received_at` em `p2p_agent_telemetry`.
///
/// As consultas de ops/dashboard (overview, timeseries, ranking), a manutenção
/// de seed-plan e o job de retenção filtram por `received_at` — o momento em que
/// o servidor recebeu a amostra. Os índices existentes eram por `collected_at`
/// (relógio do agente), então essas consultas faziam varredura completa da
/// tabela + ordenação. Aditivo: apenas índices novos.
/// </summary>
[Migration(20261012_183)]
public class M183_AddP2pTelemetryReceivedAtIndexes : Migration
{
    private const string Table = "p2p_agent_telemetry";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists()) return;

        if (!Schema.Table(Table).Index("ix_p2p_telemetry_received_at").Exists())
        {
            Create.Index("ix_p2p_telemetry_received_at")
                .OnTable(Table)
                .OnColumn("received_at").Ascending();
        }

        if (!Schema.Table(Table).Index("ix_p2p_telemetry_client_received").Exists())
        {
            Create.Index("ix_p2p_telemetry_client_received")
                .OnTable(Table)
                .OnColumn("client_id").Ascending()
                .OnColumn("received_at").Ascending();
        }

        if (!Schema.Table(Table).Index("ix_p2p_telemetry_site_received").Exists())
        {
            Create.Index("ix_p2p_telemetry_site_received")
                .OnTable(Table)
                .OnColumn("site_id").Ascending()
                .OnColumn("received_at").Ascending();
        }
    }

    public override void Down()
    {
        DropIfExists("ix_p2p_telemetry_received_at");
        DropIfExists("ix_p2p_telemetry_client_received");
        DropIfExists("ix_p2p_telemetry_site_received");
    }

    private void DropIfExists(string name)
    {
        if (Schema.Table(Table).Exists() && Schema.Table(Table).Index(name).Exists())
        {
            Delete.Index(name).OnTable(Table);
        }
    }
}
