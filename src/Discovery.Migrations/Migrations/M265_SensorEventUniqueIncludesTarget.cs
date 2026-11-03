using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// A unicidade do evento de sensor precisa incluir o ALVO: uma coleta de sensor
/// multi-volume (ex.: disco "todos os volumes") gera UMA amostra por volume com
/// o MESMO runId. Sem target_key no índice, dois volumes que mudam de status no
/// mesmo ciclo violariam a unicidade e derrubariam o lote inteiro da ingestão.
/// </summary>
[Migration(20270101_265)]
public class M265_SensorEventUniqueIncludesTarget : Migration
{
    public override void Up()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_sensor_events_agent_run_kind;");
        Execute.Sql(@"
            CREATE UNIQUE INDEX ux_sensor_events_agent_run_kind
            ON agent_sensor_events (agent_id, run_id, kind, target_key);
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_sensor_events_agent_run_kind;");
        Execute.Sql(@"
            CREATE UNIQUE INDEX ux_sensor_events_agent_run_kind
            ON agent_sensor_events (agent_id, run_id, kind);
        ");
    }
}
