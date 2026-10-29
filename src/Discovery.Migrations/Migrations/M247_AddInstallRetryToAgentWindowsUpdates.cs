using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Retry com backoff e classificação de falha na instalação de patch (WU-08).
///
/// Antes, o job de auto-instalação reprocessava QUALQUER linha `Failed` na
/// execução seguinte: sem contador, sem espera e sem distinguir "disco cheio"
/// de "update não aplicável" — na prática, um laço de instalação/falha a cada
/// 15 minutos para o mesmo patch.
///
///  - `failure_class`: disk_space | store_corrupt | needs_reboot | permanent |
///    transient | unknown (derivada em PatchFailureClassifier);
///  - `attempt_count`: tentativas já feitas;
///  - `next_attempt_at`: quando o backoff vence (só para transient).
///
/// Compatibilidade: colunas NULL-able / DEFAULT 0; linha antiga fica null/0 e
/// volta a ser elegível (comportamento anterior preservado para o histórico).
/// Idempotente (checagem de Schema).
/// </summary>
[Migration(20261226_247)]
public class M247_AddInstallRetryToAgentWindowsUpdates : Migration
{
    private const string Table = "agent_windows_updates";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        AddColumnIfMissing("failure_class", "varchar(30)");
        AddColumnIfMissing("attempt_count", "int", " NOT NULL DEFAULT 0");
        AddColumnIfMissing("next_attempt_at", "timestamptz");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        foreach (var column in new[] { "next_attempt_at", "attempt_count", "failure_class" })
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
