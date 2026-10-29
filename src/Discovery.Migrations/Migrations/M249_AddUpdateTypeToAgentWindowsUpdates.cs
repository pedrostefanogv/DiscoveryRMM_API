using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Tipo do update (WU-15): `feature` | `driver` | `software`, derivado no
/// AGENTE a partir das categorias do WUA (GUID) e enviado no envelope.
///
/// Motivo: a API re-derivava "feature update" por heurística de título, o que
/// exigia manter dois parsers idênticos (agente e servidor) — divergência entre
/// eles faz a política bloquear/aprovar o update errado. Com o tipo no envelope,
/// o servidor consome o dado em vez de adivinhar.
///
/// Compatibilidade: NULL-able; agente antigo não envia e a API mantém o
/// comportamento anterior (re-derivação onde já existia).
/// </summary>
[Migration(20261228_249)]
public class M249_AddUpdateTypeToAgentWindowsUpdates : Migration
{
    private const string Table = "agent_windows_updates";
    private const string Column = "update_type";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {Column} varchar(20) NULL");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
