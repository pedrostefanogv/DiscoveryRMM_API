using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Diagnóstico do scan e sonda estendida do Windows Update no snapshot.
///
/// O agente já envia (ou passará a enviar) no envelope:
///  - <c>scan</c>: errorCode estável, trigger, source, skippedCount e
///    skippedReasons ({driver, no_identity, unreadable} → quantidade);
///  - <c>state</c>: wsusServer, auOptions, noAutoUpdate, deferralDays,
///    activeHoursStart/End, qfeInstalledCount e downloadPercent.
///
/// Antes esses campos morriam no parser/DTO e a aba "Atualizações" não
/// explicava "por que o scan descartou N itens", "qual WSUS/ring" ou "quanto
/// já baixou". Colunas tipadas (não um blob): o suporte vai filtrar por
/// estado e o site lê direto.
///
/// Compatibilidade: TODAS as colunas novas são NULL-able, exceto
/// <c>skipped_count</c> (NOT NULL DEFAULT 0 — contador, nunca "desconhecido"
/// no DTO). Agente antigo simplesmente não preenche e o valor conhecido é
/// preservado (campo ausente nunca zera).
///
/// Idempotente (checagem de Schema) no padrão das M233/M235/M238.
/// </summary>
[Migration(20261221_239)]
public class M239_AddWindowsUpdateScanDiagnostics : Migration
{
    private const string Table = "agent_windows_update_snapshots";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        AddColumnIfMissing("scan_error_code", "text");
        AddColumnIfMissing("scan_trigger", "varchar(20)");
        AddColumnIfMissing("scan_source", "varchar(20)");
        AddColumnIfMissing("skipped_count", "int", " NOT NULL DEFAULT 0");
        AddColumnIfMissing("skipped_reasons", "jsonb");
        AddColumnIfMissing("wsus_server", "varchar(500)");
        AddColumnIfMissing("au_options", "int");
        AddColumnIfMissing("no_auto_update", "boolean");
        AddColumnIfMissing("deferral_days", "int");
        AddColumnIfMissing("active_hours_start", "int");
        AddColumnIfMissing("active_hours_end", "int");
        AddColumnIfMissing("qfe_installed_count", "int");
        AddColumnIfMissing("download_percent", "int");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        foreach (var column in new[]
                 {
                     "download_percent",
                     "qfe_installed_count",
                     "active_hours_end",
                     "active_hours_start",
                     "deferral_days",
                     "no_auto_update",
                     "au_options",
                     "wsus_server",
                     "skipped_reasons",
                     "skipped_count",
                     "scan_source",
                     "scan_trigger",
                     "scan_error_code"
                 })
        {
            if (Schema.Table(Table).Column(column).Exists())
                Delete.Column(column).FromTable(Table);
        }
    }

    private void AddColumnIfMissing(string column, string sqlType, string suffix = " NULL")
    {
        if (Schema.Table(Table).Column(column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {column} {sqlType}{suffix}");
    }
}
