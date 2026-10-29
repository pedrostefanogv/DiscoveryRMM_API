using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Ação decidida pela POLÍTICA de reboot para um agente com reinício pendente
/// (decisão D02, 2026-10-29).
///
/// Antes desta coluna, `PatchPolicy.RebootPolicy` e `RebootAllowed` eram
/// gravados, editáveis na UI e não consumidos por ninguém — configuração morta.
/// Como o produto NÃO reinicia máquinas automaticamente, a política passa a
/// produzir um sinal operacional persistido aqui:
///  - `reboot_action`: None | NotifyOperator | NotifyWithWindowHint;
///  - `reboot_action_reason`: motivo legível (qual política, por que avisar);
///  - `reboot_action_at`: quando a decisão foi calculada.
///
/// Compatibilidade: todas NULL-able; agente/snapshot antigo fica null e a UI
/// mostra apenas o badge de reinício pendente. Idempotente (checagem de Schema).
/// </summary>
[Migration(20261224_245)]
public class M245_AddRebootActionToSnapshots : Migration
{
    private const string Table = "agent_windows_update_snapshots";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        AddColumnIfMissing("reboot_action", "varchar(30)");
        AddColumnIfMissing("reboot_action_reason", "varchar(200)");
        AddColumnIfMissing("reboot_action_at", "timestamptz");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        foreach (var column in new[] { "reboot_action_at", "reboot_action_reason", "reboot_action" })
        {
            if (Schema.Table(Table).Column(column).Exists())
                Delete.Column(column).FromTable(Table);
        }
    }

    private void AddColumnIfMissing(string column, string sqlType)
    {
        if (Schema.Table(Table).Column(column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {column} {sqlType} NULL");
    }
}
