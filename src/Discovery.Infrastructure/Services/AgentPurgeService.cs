using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Exclusão definitiva de um agente. Remove todas as linhas que referenciam o
/// agente em uma única transação e preserva histórico (chamados/logs)
/// desvinculando <c>agent_id</c> em vez de apagar.
///
/// A ordem importa: primeiro os dados próprios do agente (folhas), depois as
/// referências históricas que devem sobreviver e, por último, a linha em
/// <c>agents</c>. Se uma tabela futura referenciar <c>agents</c> sem FK para
/// esta lista, o DELETE final falha com violação de FK e a transação é
/// desfeita — o handler converte esse erro em mensagem clara.
/// </summary>
public class AgentPurgeService : IAgentPurgeService
{
    private readonly DiscoveryDbContext _db;
    private readonly IHeartbeatCacheService _heartbeatCache;
    private readonly IRecordingStorageCleanupService _recordingCleanup;

    public AgentPurgeService(
        DiscoveryDbContext db,
        IHeartbeatCacheService heartbeatCache,
        IRecordingStorageCleanupService recordingCleanup)
    {
        _db = db;
        _heartbeatCache = heartbeatCache;
        _recordingCleanup = recordingCleanup;
    }

    /// <summary>
    /// Tabelas cujas linhas pertencem ao agente e devem ser apagadas.
    /// Inclui tabelas sem FK (p2p/monitoramento) que ficariam órfãs.
    /// </summary>
    private static readonly string[] AgentOwnedTables =
    [
        "agent_commands",
        "agent_tokens",
        "agent_hardware_info",
        // Hardware detalhado legado/por convenção do EF (M052 removeu as
        // tabelas singulares e as _infos nunca existiram em migrations).
        // Mantidas por completude: a execução só toca nas que existirem.
        "disk_info",
        "network_adapter_info",
        "memory_module_info",
        "disk_infos",
        "network_adapter_infos",
        "printer_infos",
        "listening_port_infos",
        "agent_software_inventory",
        "agent_labels",
        "agent_label_rule_matches",
        "agent_label_change_logs",
        "agent_label_suppressions",
        "ai_chat_jobs",
        "ai_chat_sessions",
        "mcp_tool_policies",
        "app_approval_rules",
        "automation_execution_reports",
        "automation_task_definitions",
        "sync_ping_deliveries",
        "agent_update_events",
        "p2p_agent_telemetry",
        "p2p_artifact_presence",
        "agent_monitoring_events",
        "remote_sessions",
        "entity_notes",
    ];

    /// <summary>
    /// Referências históricas que devem continuar legíveis após a exclusão do
    /// agente — apenas desvinculadas (<c>agent_id = NULL</c>).
    /// </summary>
    private static readonly string[] DetachedTables =
    [
        "tickets",
        "logs",
        "auto_ticket_rule_executions",
        "ticket_remote_sessions",
        "app_approval_audits",
    ];

    public async Task PurgeAsync(Guid agentId, CancellationToken ct = default)
    {
        // Primeiro os arquivos (as linhas ainda existem para localizá-los).
        // Best-effort: uma falha de storage não impede a exclusão do agente.
        await _recordingCleanup.DeleteForAgentAsync(agentId, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var parameters = new object[] { agentId };

        // Só executa DML nas tabelas que realmente existem. Usa to_regclass (e não
        // information_schema) porque ele resolve pelo search_path exatamente como
        // os DELETE/UPDATE sem schema abaixo. Necessário porque a M052 removeu
        // disk_info/network_adapter_info/memory_module_info e o EF ainda mapeia
        // entidades para disk_infos/network_adapter_infos/printer_infos/...
        // Lista em linha (constantes internas, sem entrada de usuário) — evita
        // depender de binding de array de parâmetro no SqlQueryRaw.
        var candidates = AgentOwnedTables.Concat(DetachedTables).Distinct().ToArray();
        var arrayLiteral = string.Join(",", candidates.Select(table => "'" + table + "'"));
#pragma warning disable EF1003 // array de identificadores constantes, sem entrada externa
        var existingTables = (await _db.Database
                .SqlQueryRaw<string>(
                    "SELECT t AS \"Value\" FROM unnest(ARRAY[" + arrayLiteral + "]::text[]) AS t " +
                    "WHERE to_regclass(t) IS NOT NULL")
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
#pragma warning restore EF1003

        // Nomes de tabela vêm de constantes do próprio serviço (não de entrada do
        // usuário); o valor do agente vai como parâmetro ({0}).
#pragma warning disable EF1003 // identificador de tabela não pode ser parametrizado
        foreach (var table in AgentOwnedTables.Where(existingTables.Contains))
        {
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE FROM " + table + " WHERE agent_id = {0}",
                parameters,
                ct);
        }

        foreach (var table in DetachedTables.Where(existingTables.Contains))
        {
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE " + table + " SET agent_id = NULL WHERE agent_id = {0}",
                parameters,
                ct);
        }
#pragma warning restore EF1003

        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM agents WHERE id = {0}",
            parameters,
            ct);

        await transaction.CommitAsync(ct);

        // Best-effort: o cache de heartbeat (Redis) não deve sobreviver ao agente,
        // mas uma falha aqui não pode marcar como falha uma exclusão já commitada.
        try
        {
            await _heartbeatCache.RemoveHeartbeatAsync(agentId, CancellationToken.None);
        }
        catch
        {
            // Cache expira sozinho; o próximo get trata o agente como offline.
        }
    }
}
