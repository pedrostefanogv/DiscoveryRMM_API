using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Correcao da agregacao/duplicidade da telemetria P2P:
/// - deduplica snapshots (agent_id, collected_at) entregues em retry/restart
///   (o outbox reenvia quando a resposta se perde, gerando linha repetida);
/// - indice unico para impedir novas duplicatas;
/// - contador de pre-cargas ignoradas por estado final (trafego evitado).
/// </summary>
[Migration(20261013_185)]
public class M185_P2pTelemetryDedupAndPreloadSkip : Migration
{
    private const string Table = "p2p_agent_telemetry";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // 1. Remove duplicatas antigas (mantem o snapshot mais recente = maior id).
        // Comparacao por collected_at (relogio do agente): o mesmo snapshot reenviado
        // traz o mesmo timestamp e nao deve contar duas vezes.
        Execute.Sql($@"
            DELETE FROM {Table} a
            USING {Table} b
            WHERE a.agent_id = b.agent_id
              AND a.collected_at = b.collected_at
              AND a.id < b.id;");

        // 2. Indice unico: reenvio do mesmo snapshot nao cria nova linha.
        if (!Schema.Table(Table).Index("ux_p2p_telemetry_agent_collected").Exists())
        {
            Create.Index("ux_p2p_telemetry_agent_collected")
                .OnTable(Table)
                .OnColumn("agent_id").Ascending()
                .OnColumn("collected_at").Ascending()
                .WithOptions().Unique();
        }

        // 3. Contador de pre-cargas ignoradas por estado final.
        if (!Schema.Table(Table).Column("preload_skipped_final_state").Exists())
        {
            Alter.Table(Table)
                .AddColumn("preload_skipped_final_state").AsInt64().NotNullable().WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("preload_skipped_final_state").Exists())
            Delete.Column("preload_skipped_final_state").FromTable(Table);

        if (Schema.Table(Table).Index("ux_p2p_telemetry_agent_collected").Exists())
            Delete.Index("ux_p2p_telemetry_agent_collected").OnTable(Table);
    }
}
