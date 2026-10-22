using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Hash chain à prova de edição nas auditorias (escopo do agente B).
/// Adiciona prev_hash/entry_hash TEXT NULL às tabelas de auditoria existentes:
///   - configuration_audits (escopo "configuration-audit", M019)
///   - automation_script_audits (escopo "script-audit", M063)
/// Colunas NULL-able: entradas legadas e falhas de chain (fail-open) ficam
/// fora da prova, sem quebrar escritas existentes. Sem índice único em
/// entry_hash — mantém simples; a verificação reprocessa a chain em ordem.
/// Idempotente (checagem de Schema) no padrão das M233/M235.
/// </summary>
[Migration(20261222_241)]
public class M241_AddAuditHashChainColumns : Migration
{
    private const string ConfigurationTable = "configuration_audits";
    private const string ScriptTable = "automation_script_audits";

    public override void Up()
    {
        AddHashColumns(ConfigurationTable);
        AddHashColumns(ScriptTable);
    }

    public override void Down()
    {
        DropHashColumns(ConfigurationTable);
        DropHashColumns(ScriptTable);
    }

    private void AddHashColumns(string table)
    {
        if (!Schema.Table(table).Exists())
            return;

        if (!Schema.Table(table).Column("prev_hash").Exists())
            Alter.Table(table).AddColumn("prev_hash").AsCustom("text").Nullable();

        if (!Schema.Table(table).Column("entry_hash").Exists())
            Alter.Table(table).AddColumn("entry_hash").AsCustom("text").Nullable();
    }

    private void DropHashColumns(string table)
    {
        if (!Schema.Table(table).Exists())
            return;

        if (Schema.Table(table).Column("entry_hash").Exists())
            Delete.Column("entry_hash").FromTable(table);

        if (Schema.Table(table).Column("prev_hash").Exists())
            Delete.Column("prev_hash").FromTable(table);
    }
}