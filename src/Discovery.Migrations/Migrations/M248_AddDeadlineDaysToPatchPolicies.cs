using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Deadline de instalação por política (WU-05, decisão D03).
///
/// `deadline_days` = dias desde a publicação em que o patch DEVE estar
/// instalado. Vencido o prazo, a janela de manutenção é ampliada até o deadline
/// (instalação fora da janela recorrente, auditada no log do job). Decisão D02:
/// o deadline é sobre INSTALAÇÃO — o produto não reinicia máquinas sozinho.
///
/// Compatibilidade: NOT NULL DEFAULT 0 (0 = sem deadline), idempotente.
/// </summary>
[Migration(20261227_248)]
public class M248_AddDeadlineDaysToPatchPolicies : Migration
{
    private const string Table = "patch_policies";
    private const string Column = "deadline_days";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {Column} int NOT NULL DEFAULT 0");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
