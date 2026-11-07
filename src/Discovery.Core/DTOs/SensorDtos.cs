using System.Text.Json;
using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

// ─────────────────────────────────────────────────────────────────────────────
// Console (usuário autenticado)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Canal numérico declarado por um sensor (built-in ou script).</summary>
public class SensorChannelDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SensorChannelUnit Unit { get; set; }
    /// <summary>chart | gauge | badge | sparkline</summary>
    public string Display { get; set; } = "chart";
    public bool IsPrimary { get; set; }
}

/// <summary>Definição completa do sensor (CRUD e listagens).</summary>
public class SensorDefinitionDto
{
    public Guid Id { get; set; }
    public string? Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SensorType SensorType { get; set; }

    public AppApprovalScopeType ScopeType { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    /// <summary>
    /// Nome do cliente do escopo, resolvido em lote na listagem (evita N+1 no
    /// front). Null quando o escopo não tem cliente ou o registro não existe mais.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>Nome do site do escopo, resolvido em lote na listagem.</summary>
    public string? SiteName { get; set; }

    /// <summary>
    /// Nome de exibição do agente do escopo (DisplayName com fallback para o
    /// hostname), resolvido em lote na listagem.
    /// </summary>
    public string? AgentName { get; set; }

    public IReadOnlyList<string> IncludeTags { get; set; } = [];
    public IReadOnlyList<string> ExcludeTags { get; set; } = [];

    public string? Target { get; set; }
    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public SensorChannelUnit ThresholdUnit { get; set; }
    public SensorComparison Comparison { get; set; }

    public string? OptionsJson { get; set; }
    public Guid? ScriptId { get; set; }
    public string? ScriptName { get; set; }
    public IReadOnlyList<SensorChannelDto> Channels { get; set; } = [];
    public string? ExtractorsJson { get; set; }
    public string? ExitCodeMapJson { get; set; }
    public string? ChartJson { get; set; }

    public int IntervalSeconds { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool StoreSamples { get; set; }
    public int FailsBeforeAlert { get; set; }
    public int SustainSamples { get; set; }
    public SensorSeverity MinSeverityToAlert { get; set; }
    public SensorSeverity SeverityWhenFailing { get; set; }
    public bool AlertOnSensorError { get; set; }

    // ── Vínculo com o auto-ticket ─────────────────────────────────────────
    /// <summary>Never / OnEscalation (default) / Always.</summary>
    public SensorAutoTicketPolicy AutoTicketPolicy { get; set; } = SensorAutoTicketPolicy.OnEscalation;
    /// <summary>Duração mínima própria (segundos). 0 = herda a política.</summary>
    public int AutoTicketMinDurationSeconds { get; set; }
    /// <summary>Low / Medium (default) / High / Critical.</summary>
    public AutoTicketImpact AutoTicketImpact { get; set; } = AutoTicketImpact.Medium;

    public int DisplayOrder { get; set; }
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>True quando o sensor vem de um escopo superior (não é do próprio agente).</summary>
    public bool IsInherited { get; set; }
    public AppApprovalScopeType? SourceScope { get; set; }
}

/// <summary>
/// Página de definições de sensor. Mantém <c>items</c>/<c>count</c> (contrato
/// antigo) e acrescenta o total real, para o cliente paginar sem truncar.
/// </summary>
public class SensorDefinitionPageDto
{
    public IReadOnlyList<SensorDefinitionDto> Items { get; set; } = [];

    /// <summary>Quantidade de itens nesta página (mesmo valor de <c>items.length</c>).</summary>
    public int Count { get; set; }

    /// <summary>Total de definições que casam com os filtros, antes do recorte de página.</summary>
    public int Total { get; set; }

    /// <summary>Página retornada (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Tamanho de página efetivamente aplicado.</summary>
    public int PageSize { get; set; }
}

/// <summary>Criação/edição de sensor. Campos não informados mantêm o valor atual no update.</summary>
public class SensorUpsertRequest
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public SensorType SensorType { get; set; } = SensorType.Script;

    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Agent;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public IReadOnlyList<string>? IncludeTags { get; set; }
    public IReadOnlyList<string>? ExcludeTags { get; set; }

    public string? Target { get; set; }
    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public SensorChannelUnit? ThresholdUnit { get; set; }
    public SensorComparison? Comparison { get; set; }

    public string? OptionsJson { get; set; }
    public Guid? ScriptId { get; set; }
    public IReadOnlyList<SensorChannelDto>? Channels { get; set; }
    public string? ExtractorsJson { get; set; }
    public string? ExitCodeMapJson { get; set; }
    public string? ChartJson { get; set; }

    public int? IntervalSeconds { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool? StoreSamples { get; set; }
    public int? FailsBeforeAlert { get; set; }
    public int? SustainSamples { get; set; }
    public SensorSeverity? MinSeverityToAlert { get; set; }
    public SensorSeverity? SeverityWhenFailing { get; set; }
    public bool? AlertOnSensorError { get; set; }

    /// <summary>Never / OnEscalation / Always.</summary>
    public SensorAutoTicketPolicy? AutoTicketPolicy { get; set; }
    /// <summary>Duração mínima própria (segundos). 0 = herda.</summary>
    public int? AutoTicketMinDurationSeconds { get; set; }
    /// <summary>Low / Medium / High / Critical.</summary>
    public AutoTicketImpact? AutoTicketImpact { get; set; }

    public int? DisplayOrder { get; set; }
    public bool? IsPinned { get; set; }
    public bool? IsActive { get; set; }
}

public class SensorCountsDto
{
    public int Total { get; set; }
    public int Passing { get; set; }
    public int Warning { get; set; }
    public int Failing { get; set; }
    public int Pending { get; set; }
    public int Unknown { get; set; }
    public int Stale { get; set; }
}

/// <summary>Item da aba "Sensores" do detalhe do agente (definição + estado).</summary>
public class SensorItemDto
{
    public Guid SensorId { get; set; }
    public string? Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SensorType SensorType { get; set; }
    public string? Target { get; set; }
    public string TargetKey { get; set; } = string.Empty;

    public AppApprovalScopeType ScopeType { get; set; }
    public bool IsInherited { get; set; }
    public AppApprovalScopeType? SourceScope { get; set; }

    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public SensorChannelUnit ThresholdUnit { get; set; }
    public SensorComparison Comparison { get; set; }

    public IReadOnlyList<SensorChannelDto> Channels { get; set; } = [];
    public IReadOnlyDictionary<string, double> CurrentChannels { get; set; } = new Dictionary<string, double>();
    public string? PrimaryChannel { get; set; }
    public double? PrimaryValue { get; set; }
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Sparkline { get; set; } = new Dictionary<string, IReadOnlyList<double>>();

    public SensorStatus Status { get; set; }
    public SensorSeverity? Severity { get; set; }
    public string? Message { get; set; }
    public bool IsStale { get; set; }
    public int? ExitCode { get; set; }
    public int? DurationMs { get; set; }
    public string? OutputExcerpt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long TotalRuns { get; set; }
    public long TotalFailures { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? LastStatusChangedAt { get; set; }
    public DateTime? LastAlertedAt { get; set; }
    public int IntervalSeconds { get; set; }
    public int TimeoutSeconds { get; set; }
    public int FailsBeforeAlert { get; set; }
    public int SustainSamples { get; set; }
    public SensorSeverity MinSeverityToAlert { get; set; }
    public Guid? ScriptId { get; set; }
    public string? ScriptName { get; set; }
    public string? ChartJson { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsPinned { get; set; }
}

public class AgentSensorsPayloadDto
{
    public Guid AgentId { get; set; }
    public string SensorFingerprint { get; set; } = string.Empty;
    public string? LastSyncedSensorFingerprint { get; set; }
    public DateTime? LastSensorSyncAt { get; set; }
    public bool UpToDate { get; set; }
    public DateTime GeneratedAt { get; set; }
    public int DefaultIntervalSeconds { get; set; }
    public int StaleAfterSeconds { get; set; }
    public SensorCountsDto Counts { get; set; } = new();
    public IReadOnlyList<SensorItemDto> Items { get; set; } = [];
}

// ── Série temporal (gráficos) ────────────────────────────────────────────────

public class SensorSeriesPointDto
{
    public DateTime T { get; set; }
    public double? V { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}

public class SensorSeriesStatsDto
{
    public double? Min { get; set; }
    public double? Avg { get; set; }
    public double? Max { get; set; }
    public double? P95 { get; set; }
    public double? Last { get; set; }
    public int Count { get; set; }
}

public class SensorSeriesChannelDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SensorChannelUnit Unit { get; set; }
    public IReadOnlyList<SensorSeriesPointDto> Points { get; set; } = [];
    public SensorSeriesStatsDto Stats { get; set; } = new();
}

public record SensorStatusStripDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public SensorStatus Status { get; set; }
}

public class SensorSeriesDto
{
    public Guid AgentId { get; set; }
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    /// <summary>raw | hourly | daily</summary>
    public string Resolution { get; set; } = "raw";
    public int StepSeconds { get; set; }
    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public SensorComparison Comparison { get; set; }
    public SensorChannelUnit ThresholdUnit { get; set; }
    public IReadOnlyList<SensorSeriesChannelDto> Channels { get; set; } = [];
    public IReadOnlyList<SensorStatusStripDto> StatusStrips { get; set; } = [];
}

public class SensorEventDto
{
    public Guid Id { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public SensorEventKind Kind { get; set; }
    public SensorStatus Status { get; set; }
    public SensorSeverity? Severity { get; set; }
    public SensorStatus? PreviousStatus { get; set; }
    public SensorSeverity? PreviousSeverity { get; set; }
    public string? Message { get; set; }
    public int? ExitCode { get; set; }
    public DateTime OccurredAt { get; set; }
}

public class SensorEventPageDto
{
    public IReadOnlyList<SensorEventDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
}

/// <summary>Contadores da frota (página "Sensores" e dashboards).</summary>
public class SensorFleetCountsDto
{
    public int Total { get; set; }
    public int Passing { get; set; }
    public int Warning { get; set; }
    public int Failing { get; set; }
    public int Pending { get; set; }
    public int Unknown { get; set; }
    public int Stale { get; set; }
    /// <summary>Agentes distintos com pelo menos um sensor em falha.</summary>
    public int AgentsWithFailing { get; set; }
}

public class SensorFleetByTypeDto
{
    public SensorType SensorType { get; set; }
    public int Total { get; set; }
    public int Warning { get; set; }
    public int Failing { get; set; }
}

/// <summary>Agente com mais sensores fora do normal (top da visão de frota).</summary>
public class SensorFleetAgentDto
{
    public Guid AgentId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public Guid? ClientId { get; set; }
    public string? ClientName { get; set; }
    public Guid? SiteId { get; set; }
    public string? SiteName { get; set; }
    public int Failing { get; set; }
    public int Warning { get; set; }
    public SensorSeverity? WorstSeverity { get; set; }
}

public class SensorFleetSummaryDto
{
    public DateTime GeneratedAt { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public SensorFleetCountsDto Counts { get; set; } = new();
    public IReadOnlyList<SensorFleetByTypeDto> ByType { get; set; } = [];
    public IReadOnlyList<SensorFleetAgentDto> Top { get; set; } = [];
}

/// <summary>Resposta do "executar agora" (um comando por agente).</summary>
public class SensorRunResponseDto
{
    public Guid CommandId { get; set; }
    public bool Dispatched { get; set; }
    public int SensorCount { get; set; }
    public string? Message { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Agent-auth (contrato do agente Go)
// ─────────────────────────────────────────────────────────────────────────────

public class AgentSensorSyncRequest
{
    public string? KnownSensorFingerprint { get; set; }
    public bool IncludeScriptContent { get; set; }
}

public class AgentSensorScriptRefDto
{
    public Guid ScriptId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public AutomationScriptType ScriptType { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public string ContentHashSha256 { get; set; } = string.Empty;
    public string? Content { get; set; }
}

/// <summary>
/// Item da policy de sensores entregue ao agente. Contém apenas o necessário
/// para COLETAR (tipo, alvo, opções, extratores e script). Limiares e
/// configuração de alerta NÃO são enviados: a decisão é do servidor.
/// </summary>
public class AgentSensorPolicyDto
{
    public Guid SensorId { get; set; }
    public string? Key { get; set; }
    public string Name { get; set; } = string.Empty;
    public SensorType SensorType { get; set; }
    public string? Target { get; set; }
    public int IntervalSeconds { get; set; }
    public int TimeoutSeconds { get; set; }

    /// <summary>Opções de coleta específicas do tipo (JSON, repassado ao agente).</summary>
    public string? OptionsJson { get; set; }

    public IReadOnlyList<SensorChannelDto> Channels { get; set; } = [];
    public string? ExtractorsJson { get; set; }
    public string? ExitCodeMapJson { get; set; }
    public AgentSensorScriptRefDto? Script { get; set; }
}

public class AgentSensorSyncResponse
{
    public bool UpToDate { get; set; }
    public string SensorFingerprint { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public int SensorCount { get; set; }
    public int DefaultIntervalSeconds { get; set; }
    public IReadOnlyList<AgentSensorPolicyDto> Sensors { get; set; } = [];
}

/// <summary>
/// Evidência crua coletada pelo agente. NÃO inclui status/severidade — o
/// servidor avalia (fonte única de verdade).
/// </summary>
public class AgentSensorMeasureDto
{
    // Disco
    public double? FreePercent { get; set; }
    public double? UsedPercent { get; set; }
    public double? TotalBytes { get; set; }
    public double? FreeBytes { get; set; }

    // Serviço / processo
    public string? ServiceState { get; set; }
    public string? ProcessState { get; set; }
    public bool? Running { get; set; }
    public int? ProcessCount { get; set; }
    public double? WorkingSetBytes { get; set; }

    // Ping
    public int? Replies { get; set; }
    public int? Sent { get; set; }
    public double? AvgRttMs { get; set; }
    public double? LatencyMinMs { get; set; }
    public double? LatencyMaxMs { get; set; }
    public double? JitterMs { get; set; }
    public double? LossPercent { get; set; }

    // HTTP / TCP / DNS
    public int? StatusCode { get; set; }
    public double? DnsMs { get; set; }
    public double? ConnectMs { get; set; }
    public double? TlsMs { get; set; }
    public double? TtfbMs { get; set; }
    public double? TotalMs { get; set; }
    public double? Bytes { get; set; }

    // Throughput / uptime / banda / disco I/O
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }
    public double? UptimeSeconds { get; set; }
    public double? ResolveMs { get; set; }
    public double? RxBps { get; set; }
    public double? TxBps { get; set; }
    public double? ReadBps { get; set; }
    public double? WriteBps { get; set; }
    public double? BusyPercent { get; set; }
    public double? DiskLatencyMs { get; set; }

    // Script
    public int? ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }

    // ── Fase 5 ──────────────────────────────────────────────────────────────

    // Log de eventos (contagem em uma janela)
    /// <summary>Quantidade de eventos que casam com o filtro na janela.</summary>
    public int? EventCount { get; set; }
    /// <summary>Tamanho da janela consultada (minutos).</summary>
    public int? EventWindowMinutes { get; set; }
    /// <summary>Instante do evento mais recente que casou com o filtro.</summary>
    public DateTime? LastEventAt { get; set; }

    // SMART / temperatura do disco
    /// <summary>Healthy | Warning | Unhealthy | Unknown (resumo do SMART).</summary>
    public string? SmartStatus { get; set; }
    public double? TemperatureC { get; set; }
    public int? PowerOnHours { get; set; }
    public int? ReallocatedSectors { get; set; }

    // Certificado (validade)
    public double? DaysToExpiry { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? CertificateSubject { get; set; }

    // Antivírus (Defender)
    public bool? DefenderEnabled { get; set; }
    public bool? DefenderRealTimeEnabled { get; set; }
    public double? SignatureAgeDays { get; set; }
    public string? SignatureVersion { get; set; }

    // BitLocker (criptografia de volume)
    /// <summary>On | Off | Suspended | Unknown.</summary>
    public string? BitLockerStatus { get; set; }
    public double? BitLockerEncryptionPercent { get; set; }

    // Consistência de AD (ADConsistency)
    //
    // A evidência crua deste tipo viaja historicamente no mapa Channels (o
    // agente Go publica ad_collect_ok, replication_failures, ... porque o
    // SensorMeasure não tinha campos para AD). Estes campos espelham os
    // mesmos valores para que o EXTRATOR do servidor tenha fonte própria — sem
    // eles o catálogo declararia canais que o DeriveChannels nunca emitiria,
    // que é exatamente a divergência que SensorChannelCatalogTests barra.
    // Net: o agente continua mandando Channels; o servidor prefere o campo
    // explícito e cai para o canal quando ele faltar.
    /// <summary>1 = a coleta conseguiu consultar o AD; 0 = não conseguiu.</summary>
    public bool? AdCollectOk { get; set; }
    /// <summary>Nº de falhas de replicação no repadmin /showrepl.</summary>
    public int? AdReplicationFailures { get; set; }
    /// <summary>true = replicação sem falhas.</summary>
    public bool? AdReplicationOk { get; set; }
    /// <summary>Nº de testes do dcdiag que falharam.</summary>
    public int? AdDcdiagFailedTests { get; set; }
    /// <summary>true = nenhum teste do dcdiag falhou.</summary>
    public bool? AdDcdiagOk { get; set; }
    /// <summary>Desvio de relógio em segundos (|v| > 15 quebra Kerberos).</summary>
    public double? AdTimeOffsetSeconds { get; set; }
    /// <summary>true = relógio dentro da tolerância.</summary>
    public bool? AdClockOk { get; set; }

    // Serviço: tentativa de restart (opção restartIfStopped)
    public bool? RestartAttempted { get; set; }
    public bool? RestartSucceeded { get; set; }
}

public class AgentSensorResultDto
{
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public Guid RunId { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int? DurationMs { get; set; }

    public AgentSensorMeasureDto Measure { get; set; } = new();

    /// <summary>
    /// Canais já extraídos no agente (protocolo MDZ_METRIC / extratores). Para
    /// sensores built-in é opcional: o servidor deriva de Measure.
    /// </summary>
    public Dictionary<string, double>? Channels { get; set; }

    /// <summary>Erro de coleta (o servidor marca Unknown + evento RunError).</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Status declarado pelo PRÓPRIO script (linha MDZ_STATUS do stdout). O
    /// servidor valida e traduz (OK/WARNING/CRITICAL/UNKNOWN); não substitui a
    /// avaliação de limiares quando estes estão configurados.
    /// </summary>
    public string? DeclaredStatus { get; set; }

    /// <summary>Mensagem declarada pelo script (linha MDZ_MESSAGE).</summary>
    public string? DeclaredMessage { get; set; }
}

public class AgentSensorResultBatch
{
    public Guid AgentId { get; set; }
    public string? CorrelationId { get; set; }
    public IReadOnlyList<AgentSensorResultDto> Samples { get; set; } = [];
}

public class AgentSensorRejectedDto
{
    public Guid SensorId { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class AgentSensorResultResponse
{
    public int Accepted { get; set; }
    public IReadOnlyList<AgentSensorRejectedDto> Rejected { get; set; } = [];
}
