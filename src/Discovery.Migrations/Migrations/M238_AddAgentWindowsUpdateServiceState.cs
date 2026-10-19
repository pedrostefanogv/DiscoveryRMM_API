using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Sonda de estado do Windows Update no snapshot do agente.
///
/// O agente passa a mandar no envelope um bloco OPCIONAL <c>state</c> com o
/// estado do serviço (wuauserv), conectividade com o Windows Update Online,
/// política/WSUS, última instalação bem-sucedida, download em andamento e a
/// assinatura que dispara o próximo scan. Sem essas colunas a aba
/// "Atualizações" não consegue explicar um scan que volta vazio
/// (ex.: wuauserv desabilitado → ErrServiceDisabled no agente).
///
/// Decisão de schema: colunas tipadas em <c>agent_windows_update_snapshots</c>
/// (e não um blob jsonb em <c>pending_reboot_reasons</c>): são poucos campos,
/// a UI lê direto e o suporte vai filtrar por estado do serviço. Todas as
/// colunas são NULL-able — agente antigo simplesmente não preenche e o valor
/// conhecido é preservado (nunca zerado por campo ausente).
///
/// Idempotente (IF NOT EXISTS / checagem de Schema) no padrão das M233/M235.
/// </summary>
[Migration(20261221_238)]
public class M238_AddAgentWindowsUpdateServiceState : Migration
{
    private const string Table = "agent_windows_update_snapshots";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        AddColumnIfMissing("service_state", "varchar(20)");
        AddColumnIfMissing("wu_online", "boolean");
        AddColumnIfMissing("policy_managed", "boolean");
        AddColumnIfMissing("last_install_success_at", "timestamptz");
        AddColumnIfMissing("download_in_progress", "boolean");
        AddColumnIfMissing("state_hash", "varchar(80)");
        AddColumnIfMissing("state_changed_at", "timestamptz");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        foreach (var column in new[]
                 {
                     "state_changed_at",
                     "state_hash",
                     "download_in_progress",
                     "last_install_success_at",
                     "policy_managed",
                     "wu_online",
                     "service_state"
                 })
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
