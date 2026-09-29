using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Indice parcial para a varredura de reentrega de comandos
/// (PendingCommandRedeliveryService).
///
/// A consulta filtra agent_id (agentes online) + status nao-terminal e compara
/// created_at/sent_at. Os indices existentes sao de coluna unica
/// (ix_commands_agent_id, ix_commands_status), o que obriga o planner a combinar
/// dois bitmaps; o indice parcial cobre exatamente o predicado que importa e
/// ignora os comandos ja finalizados (a maioria da tabela).
///
/// Aditivo: apenas um indice novo.
/// </summary>
[Migration(20261022_189)]
public class M189_AddAgentCommandRedeliveryIndex : Migration
{
    private const string Table = "agent_commands";
    private const string IndexName = "ix_agent_commands_redelivery";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // Status nao-terminais: Pending=0, Sent=1, Running=2.
        Execute.Sql($@"
            CREATE INDEX IF NOT EXISTS {IndexName}
            ON {Table} (agent_id, created_at)
            WHERE status IN (0, 1, 2);");
    }

    public override void Down()
    {
        Execute.Sql($"DROP INDEX IF EXISTS {IndexName};");
    }
}
