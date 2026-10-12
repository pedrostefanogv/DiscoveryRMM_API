using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Configuration;

/// <summary>
/// Centralized retention and database maintenance settings.
/// Persisted in ServerConfiguration.RetentionSettingsJson.
/// </summary>
public class RetentionSettings
{
    // ── Data retention (days) ──

    /// <summary>Days to keep log entries. Default: 90.</summary>
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>Days to keep read notifications. Unread notifications are never purged. Default: 60.</summary>
    public int NotificationRetentionDays { get; set; } = 60;

    /// <summary>
    /// Dias sem atividade antes de descartar uma inscricao de Web Push.
    /// Default: 120. O provedor so sinaliza inscricao morta (HTTP 410) quando
    /// tentamos enviar; usuarios que nunca mais recebem notificacoes deixariam
    /// linhas orfas para sempre. Usuarios ativos renovam "last_seen_at" a cada
    /// carga do console.
    /// </summary>
    public int PushSubscriptionRetentionDays { get; set; } = 120;

    /// <summary>Days to keep completed/failed agent commands. Default: 30.</summary>
    public int AgentCommandRetentionDays { get; set; } = 30;

    /// <summary>Days to keep expired/revoked user sessions. Default: 30.</summary>
    public int SessionRetentionDays { get; set; } = 30;

    /// <summary>Grace period (days) after token expiry before hard delete. Default: 7.</summary>
    public int TokenExpiredGraceDays { get; set; } = 7;

    /// <summary>Days to keep sync ping delivery records. Default: 7.</summary>
    public int SyncPingRetentionDays { get; set; } = 7;

    /// <summary>
    /// Days to keep P2P agent telemetry. Default: 30 — alinhado com a maior
    /// janela (30d) do card de Métricas P2P do dashboard. Com 7 dias, a janela
    /// de 30d exibiria apenas 1/4 do período rotulado.
    /// </summary>
    public int TelemetryRetentionDays { get; set; } = 30;

    /// <summary>Days to keep automation execution reports. Default: 30.</summary>
    public int AutomationReportRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days to keep the agent label change history (auditoria de labels).
    /// Default: 90. A tabela cresce junto com o volume de mudancas de labels.
    /// </summary>
    public int AgentLabelChangeLogRetentionDays { get; set; } = 90;

    // ── AI Chat (kept in sync with AiChatRetentionJob) ──

    /// <summary>Days before soft-deleting AI chat sessions. Default: 180.</summary>
    public int AiChatExpiryDays { get; set; } = 180;

    /// <summary>Grace period after soft delete before hard delete. Default: 30.</summary>
    public int AiChatGraceDays { get; set; } = 30;

    // ── Database Maintenance ──

    public DatabaseMaintenanceSettings? DatabaseMaintenance { get; set; }
}

/// <summary>Escopos de VACUUM FULL aceitos pela configuração.</summary>
public static class DatabaseMaintenanceScopes
{
    /// <summary>Somente tabelas acima do limiar de tuplas mortas (recomendado).</summary>
    public const string Bloated = "bloated";

    /// <summary>Todas as tabelas (janela de manutenção pesada).</summary>
    public const string All = "all";

    /// <summary>Desliga a fase (equivale a flag desligada).</summary>
    public const string None = "none";
}

/// <summary>Escopos de REINDEX aceitos pela configuração.</summary>
public static class DatabaseReindexScopes
{
    /// <summary>Somente índices inválidos (sempre repara estes, independente da flag).</summary>
    public const string Invalid = "invalid";

    /// <summary>Todos os índices das tabelas do escopo (janela de manutenção pesada).</summary>
    public const string All = "all";

    /// <summary>Desliga a fase (índices inválidos continuam sendo reparados).</summary>
    public const string None = "none";
}

/// <summary>
/// Manutenção física do PostgreSQL (VACUUM/REINDEX/ANALYZE/FREEZE) agendada pelo
/// <c>DatabaseMaintenanceJob</c>. Todos os campos têm default seguro: a rotina
/// leve (VACUUM ANALYZE) é ligada e as operações com lock exclusivo (VACUUM FULL,
/// REINDEX de tudo) ficam desligadas até o operador optar por elas.
/// </summary>
public class DatabaseMaintenanceSettings
{
    /// <summary>Enable scheduled database maintenance. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Quartz cron expression for maintenance window.
    /// Default: "0 0 3 ? * SUN" (Sunday 3 AM). Aplicada no startup pela API.
    /// </summary>
    public string Schedule { get; set; } = "0 0 3 ? * SUN";

    /// <summary>
    /// VACUUM FULL (reescreve a tabela, lock exclusivo). Default: false — só
    /// ligue para janelas pesadas; use <see cref="VacuumFullScope"/> = "bloated"
    /// para atingir apenas tabelas com muitas tuplas mortas.
    /// </summary>
    public bool VacuumFull { get; set; } = false;

    /// <summary>Escopo do VACUUM FULL: "bloated" (default) ou "all".</summary>
    public string VacuumFullScope { get; set; } = DatabaseMaintenanceScopes.Bloated;

    /// <summary>Limiar (%) de tuplas mortas sobre vivas para escolher o VACUUM FULL. Default: 20.</summary>
    public int VacuumFullBloatPercent { get; set; } = 20;

    /// <summary>Executa a fase de REINDEX (concorrente por default). Default: true.</summary>
    public bool Reindex { get; set; } = true;

    /// <summary>
    /// Escopo do REINDEX: "invalid" (default, só índices quebrados), "all" ou "none".
    /// Índices inválidos (<c>pg_index.indisvalid = false</c>) são SEMPRE reparados,
    /// mesmo com a flag desligada — isso é integridade, não tuning.
    /// </summary>
    public string ReindexScope { get; set; } = DatabaseReindexScopes.Invalid;

    /// <summary>Usa REINDEX ... CONCURRENTLY (não bloqueia escrita). Default: true.</summary>
    public bool ReindexConcurrently { get; set; } = true;

    /// <summary>Executa ANALYZE (atualiza estatísticas do planner). Default: true.</summary>
    public bool Analyze { get; set; } = true;

    /// <summary>Só reanalisa tabelas sem estatística recente (dias). Default: 3.</summary>
    public int AnalyzeStaleAfterDays { get; set; } = 3;

    /// <summary>VACUUM ANALYZE em todas as tabelas (rotina leve, não bloqueia escrita). Default: true.</summary>
    public bool VacuumAnalyze { get; set; } = true;

    /// <summary>VACUUM (FREEZE) nas tabelas com XID antigo (proteção contra wraparound). Default: true.</summary>
    public bool VacuumFreeze { get; set; } = true;

    /// <summary>
    /// Idade mínima de XID (age(relfrozenxid)) para disparar o VACUUM FREEZE.
    /// Default: 100.000.000 (metade do autovacuum_freeze_max_age padrão de 200M).
    /// </summary>
    public long FreezeMinXidAge { get; set; } = 100_000_000;

    /// <summary>lock_timeout por comando, em segundos (falha em vez de travar horas). Default: 5.</summary>
    public int LockTimeoutSeconds { get; set; } = 5;

    /// <summary>maintenance_work_mem da sessão do job, em MB. Default: 256.</summary>
    public int MaintenanceWorkMemMb { get; set; } = 256;

    /// <summary>Teto de tabelas processadas por execução (0 = sem limite). Default: 0.</summary>
    public int MaxTablesPerRun { get; set; } = 0;

    /// <summary>
    /// Cria índices para foreign keys que não têm índice com a coluna de origem
    /// como primeira coluna (diagnóstico em <c>GET /admin/database-maintenance/health</c>).
    /// Criação <c>CONCURRENTLY</c> e idempotente (<c>IF NOT EXISTS</c>), mas ainda
    /// é mudança de schema — por isso o default é false e a ativação é explícita.
    /// </summary>
    public bool CreateMissingForeignKeyIndexes { get; set; } = false;

    /// <summary>Prefixo dos índices de FK criados. Default: "ix".</summary>
    public string ForeignKeyIndexPrefix { get; set; } = "ix";

    /// <summary>
    /// Normaliza os valores para faixas seguras. Nunca lança: configuração
    /// corrompida cai no default do campo.
    /// </summary>
    public DatabaseMaintenanceSettings Normalize()
    {
        Schedule = string.IsNullOrWhiteSpace(Schedule) ? "0 0 3 ? * SUN" : Schedule.Trim();
        VacuumFullScope = NormalizeScope(VacuumFullScope, DatabaseMaintenanceScopes.Bloated,
            [DatabaseMaintenanceScopes.Bloated, DatabaseMaintenanceScopes.All, DatabaseMaintenanceScopes.None]);
        ReindexScope = NormalizeScope(ReindexScope, DatabaseReindexScopes.Invalid,
            [DatabaseReindexScopes.Invalid, DatabaseReindexScopes.All, DatabaseReindexScopes.None]);
        VacuumFullBloatPercent = Math.Clamp(VacuumFullBloatPercent, 1, 1000);
        AnalyzeStaleAfterDays = Math.Clamp(AnalyzeStaleAfterDays, 0, 3650);
        FreezeMinXidAge = Math.Clamp(FreezeMinXidAge, 1_000_000, 2_000_000_000);
        LockTimeoutSeconds = Math.Clamp(LockTimeoutSeconds, 0, 3600);
        MaintenanceWorkMemMb = Math.Clamp(MaintenanceWorkMemMb, 16, 4096);
        MaxTablesPerRun = Math.Max(0, MaxTablesPerRun);
        ForeignKeyIndexPrefix = string.IsNullOrWhiteSpace(ForeignKeyIndexPrefix)
            ? "ix"
            : ForeignKeyIndexPrefix.Trim().ToLowerInvariant();
        return this;
    }

    /// <summary>
    /// Lê a seção de manutenção do <c>RetentionSettingsJson</c>. JSON vazio ou
    /// sem a seção devolve os defaults (Enabled = true), alinhado com a
    /// documentação do campo.
    /// </summary>
    public static DatabaseMaintenanceSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return new DatabaseMaintenanceSettings();

        try
        {
            var settings = JsonSerializer.Deserialize<RetentionSettings>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return (settings?.DatabaseMaintenance ?? new DatabaseMaintenanceSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new DatabaseMaintenanceSettings();
        }
    }

    private static string NormalizeScope(string? value, string fallback, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var normalized = value.Trim().ToLowerInvariant();
        return Array.Exists(allowed, a => a == normalized) ? normalized : fallback;
    }
}
