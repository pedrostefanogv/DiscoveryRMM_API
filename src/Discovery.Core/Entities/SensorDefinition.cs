using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Definição de um sensor (unidade de monitoramento periódico). Produz estado
/// (Passing/Warning/Failing/Unknown) e canais numéricos que alimentam gráficos.
///
/// Escopo: Global/Client/Site/Agent (AppApprovalScopeType) + filtro por tags —
/// o mesmo modelo dos AutomationTaskDefinition, resolvido no sync do agente.
/// Quem decide o resultado é o SERVIDOR (o agente envia apenas a evidência).
/// </summary>
public class SensorDefinition
{
    public Guid Id { get; set; }

    /// <summary>Chave normalizada [a-z0-9_-], única por escopo entre ativas.</summary>
    public string? Key { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SensorType SensorType { get; set; } = SensorType.Script;

    // ── Escopo ────────────────────────────────────────────────────────────
    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Agent;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    /// <summary>Filtro include/exclude por label (JSON array de strings).</summary>
    public string? IncludeTagsJson { get; set; }
    public string? ExcludeTagsJson { get; set; }

    // ── Alvo e limiares ──────────────────────────────────────────────────
    /// <summary>
    /// Alvo do sensor: letra de volume ("C:"), nome de serviço/processo,
    /// host/IP, URL, host:porta ou interface. Vazio em DiskSpace = todos os
    /// volumes fixos.
    /// </summary>
    public string? Target { get; set; }

    public double? WarningThreshold { get; set; }
    public double? CriticalThreshold { get; set; }
    public SensorChannelUnit ThresholdUnit { get; set; } = SensorChannelUnit.Percent;
    public SensorComparison Comparison { get; set; } = SensorComparison.HigherIsWorse;

    /// <summary>Opções específicas do tipo (JSON) — ver comentário em SensorOptionsJson.</summary>
    public string? OptionsJson { get; set; }

    // ── Script ────────────────────────────────────────────────────────────
    public Guid? ScriptId { get; set; }

    // ── Canais e extratores (Script) ─────────────────────────────────────
    /// <summary>Canais declarados (JSON array) — ver SensorChannelDefinition.</summary>
    public string? ChannelsJson { get; set; }
    /// <summary>Extratores de métrica (JSON array) — regex/JSON/mdz.</summary>
    public string? ExtractorsJson { get; set; }
    /// <summary>Mapa de exit codes -> status/severidade (JSON array).</summary>
    public string? ExitCodeMapJson { get; set; }
    /// <summary>Configuração do gráfico (JSON): unidade, yMin/yMax, banda.</summary>
    public string? ChartJson { get; set; }

    // ── Cadência e alertas ────────────────────────────────────────────────
    /// <summary>0 = herda o default do servidor (agent_sensor_interval_seconds).</summary>
    public int IntervalSeconds { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Quando false, o agente não envia amostras (só estado/eventos).</summary>
    public bool StoreSamples { get; set; } = true;

    public int FailsBeforeAlert { get; set; } = 2;

    /// <summary>
    /// Amostras ruins consecutivas exigidas para ESCALAR a severidade de um
    /// sensor que já está falhando (ex.: Warning → Failing). 1 = escala na hora
    /// (default). A primeira falha de um sensor que estava OK continua imediata:
    /// histerese aqui serve para matar flapping entre níveis, não para esconder
    /// problema novo.
    /// </summary>
    public int SustainSamples { get; set; } = 1;
    public SensorSeverity MinSeverityToAlert { get; set; } = SensorSeverity.Warning;
    public SensorSeverity SeverityWhenFailing { get; set; } = SensorSeverity.Critical;
    /// <summary>Alerta também quando o próprio sensor falha em coletar ("sensor quebrado").</summary>
    public bool AlertOnSensorError { get; set; } = true;

    // ── Auto-ticket por sensor (M268) ─────────────────────────────────────
    /// <summary>
    /// Vínculo do sensor com o auto-ticket. Never vence as allowlists da
    /// política hierárquica. Default OnEscalation preserva o comportamento
    /// anterior (só abre na abertura/escala).
    /// </summary>
    public SensorAutoTicketPolicy AutoTicketPolicy { get; set; } = SensorAutoTicketPolicy.OnEscalation;

    /// <summary>
    /// Duração mínima (segundos) em falha antes de abrir chamado para ESTE
    /// sensor. 0 = herda o valor da política hierárquica.
    /// </summary>
    public int AutoTicketMinDurationSeconds { get; set; }

    /// <summary>
    /// Impacto do sensor. Alimenta a escada de decisão (magnitude) e serve de
    /// piso para a prioridade sugerida pela IA.
    /// </summary>
    public AutoTicketImpact AutoTicketImpact { get; set; } = AutoTicketImpact.Medium;

    public int DisplayOrder { get; set; } = 50;
    public bool IsPinned { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}
