using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Enums;

namespace Discovery.Core.Configuration;

/// <summary>
/// Política de auto-ticket com HERANÇA HIERÁRQUICA: Server -> Client -> Site.
///
/// TODOS os campos são anuláveis de propósito: <c>null</c> significa "herda do
/// nível superior", exatamente a semântica já usada pelo
/// <c>IConfigurationResolver</c> ("valores null em Client/Site são preenchidos
/// pelo nível superior"). Um valor não-nulo sobrescreve o nível acima.
///
/// Desligar em qualquer nível: <see cref="Enabled"/> = false. A resolução
/// curto-circuita ali e o motivo registra qual nível desligou.
///
/// Persistida por nível em <c>auto_ticket_policy_json</c> (blob JSON, mesmo
/// padrão de RetentionSettings/SensorsSettings — evita mudanças no catálogo de
/// configuração).
/// </summary>
public class AutoTicketPolicy
{
    // ── Liga/desliga e modo ────────────────────────────────────────────────

    /// <summary>false em QUALQUER nível = nunca abre chamado naquele escopo.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Modo de operação (Off/Shadow/Canary/Active).</summary>
    public AutoTicketScopeMode? Mode { get; set; }

    /// <summary>Clientes do canário (usado quando Mode = Canary).</summary>
    public string[]? CanaryClientIds { get; set; }

    /// <summary>Sites do canário (usado quando Mode = Canary).</summary>
    public string[]? CanarySiteIds { get; set; }

    // ── Quando abrir: escada magnitude × persistência ──────────────────────

    /// <summary>Falhas consecutivas exigidas antes de abrir.</summary>
    public int? MinConsecutiveFailures { get; set; }

    /// <summary>Duração mínima (segundos) em estado de falha antes de abrir.</summary>
    public int? MinDurationSeconds { get; set; }

    /// <summary>Impacto mínimo para abrir chamado.</summary>
    public AutoTicketImpact? MinImpactToOpen { get; set; }

    /// <summary>Impacto mínimo para escalar a prioridade.</summary>
    public AutoTicketImpact? MinImpactToEscalate { get; set; }

    /// <summary>Quando true, Warning sustentado também abre chamado.</summary>
    public bool? OpenOnWarning { get; set; }

    /// <summary>
    /// Quando true, sinal preditivo (SMART, certificado, backup) também conta
    /// como incidente. O ITIL define que um evento que POSSA degradar o serviço
    /// já é incidente, então isto é o default.
    /// </summary>
    public bool? OpenOnPredictive { get; set; }

    // ── Supressão ──────────────────────────────────────────────────────────

    /// <summary>Carência (minutos) ao sair da manutenção antes de reavaliar.</summary>
    public int? MaintenanceGraceMinutes { get; set; }

    /// <summary>
    /// Ao terminar a manutenção, reavaliar e notificar o que foi suprimido.
    /// Espelha o Checkmk ("retroactively notified precisely at the end of the
    /// downtime") — sem isto, o que é suprimido na janela desaparece.
    /// </summary>
    public bool? ReNotifyAfterMaintenanceEnds { get; set; }

    /// <summary>Respeita Agent.MaintenanceEnabled.</summary>
    public bool? RespectAgentMaintenance { get; set; }

    /// <summary>Respeita MaintenanceWindowJson de Site/Client/Servidor.</summary>
    public bool? RespectMaintenanceWindow { get; set; }

    /// <summary>
    /// Não abrir chamado quando o próprio agente/caminho não está alcançável.
    /// Padrão de mercado (Checkmk: "only generates notifications for services
    /// if the host is in the UP state"; PRTG: Ping como master object).
    /// O problema do PAI continua gerando chamado.
    /// </summary>
    public bool? SuppressWhenHostNotReachable { get; set; }

    /// <summary>Supressão em cascata por dependência de agente.</summary>
    public bool? DependencySuppressionEnabled { get; set; }

    /// <summary>Profundidade máxima da recursão de dependência (guarda de ciclo).</summary>
    public int? DependencyMaxDepth { get; set; }

    /// <summary>
    /// Fechar o chamado quando a resolução chega DURANTE a manutenção.
    /// Corrige o ticket órfão: no Zabbix "recovery notifications are not sent
    /// if the problem started during maintenance".
    /// </summary>
    public bool? CloseTicketResolvedDuringMaintenance { get; set; }

    /// <summary>Janela (minutos) para detecção de flapping.</summary>
    public int? FlapWindowMinutes { get; set; }

    /// <summary>Transições na janela acima das quais o sensor é considerado flapping.</summary>
    public int? FlapTransitionsThreshold { get; set; }

    // ── Dedup e limites ────────────────────────────────────────────────────

    /// <summary>Janela de deduplicação por correlação (minutos).</summary>
    public int? DedupWindowMinutes { get; set; }

    /// <summary>
    /// Teto da janela rolante (horas). A janela estende enquanto chegam alertas,
    /// mas expira neste teto (ou na resolução) e um alerta posterior abre NOVO
    /// chamado. Espelha o PagerDuty (5 min a 24 h).
    /// </summary>
    public int? DedupWindowMaxHours { get; set; }

    /// <summary>Janela (minutos) para reabrir um chamado fechado em vez de criar outro.</summary>
    public int? ReopenWindowMinutes { get; set; }

    /// <summary>Teto de chamados criados por hora por alert code (0 = sem limite).</summary>
    public int? MaxCreatedTicketsPerHourPerAlertCode { get; set; }

    /// <summary>
    /// Incrementar um contador de ocorrências no chamado existente em vez de
    /// criar outro. Espelha o "Occurrence Count" do Kaseya: 1 chamado : N alertas.
    /// </summary>
    public bool? IncrementOccurrenceCounter { get; set; }

    // ── Vínculo por sensor ─────────────────────────────────────────────────

    /// <summary>Sensores autorizados a abrir chamado. Vazio/null = todos.</summary>
    public string[]? AllowedSensorKeys { get; set; }

    /// <summary>Sensores que NUNCA abrem chamado. Vence <see cref="AllowedSensorKeys"/>.</summary>
    public string[]? NeverTicketSensorKeys { get; set; }

    // ── Roteamento e IA ────────────────────────────────────────────────────

    /// <summary>Departamento padrão dos chamados automáticos.</summary>
    public Guid? DefaultDepartmentId { get; set; }

    /// <summary>Categoria padrão dos chamados automáticos.</summary>
    public string? DefaultCategory { get; set; }

    /// <summary>Autonomia da IA (Off/Suggest/AutoApply).</summary>
    public AutoTicketAiMode? AiMode { get; set; }

    /// <summary>Confiança mínima para a IA aplicar automaticamente.</summary>
    public double? AiMinConfidence { get; set; }

    /// <summary>Campos que a IA pode sugerir.</summary>
    public string[]? AiSuggestibleFields { get; set; }

    /// <summary>
    /// Campos de match EXATO avaliados ANTES de qualquer similaridade/LLM.
    /// Espelha o Unified Alert Grouping do PagerDuty: o gate de conteúdo é
    /// pré-condição, de modo que a IA nunca amplia o conjunto de merge.
    /// </summary>
    public string[]? AiContentGateFields { get; set; }

    /// <summary>
    /// PLUGÁVEL: se o enriquecimento pode enviar contexto do equipamento ao
    /// provedor de IA (SO, nome de serviço, métricas, limiares). Quando false,
    /// a IA só recebe dados já redigidos/agregados localmente. Configurável por
    /// nível — um cliente pode proibir e outro permitir.
    /// </summary>
    public bool? AiAllowEquipmentContext { get; set; }

    /// <summary>
    /// PLUGÁVEL: campos cujo valor é removido/redigido antes de montar o prompt
    /// (PII e segredos). Lista de nomes de chave do payload.
    /// </summary>
    public string[]? AiRedactFields { get; set; }

    /// <summary>
    /// PLUGÁVEL: hosts/nomes de serviço são substituídos por pseudônimos antes
    /// de sair da instalação. Útil quando o cliente não permite identificar
    /// ativos no provedor de IA.
    /// </summary>
    public bool? AiAnonymizeAssetNames { get; set; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Enums como NOME ("Shadow"), não como número.
        //
        // O MVC já registra JsonStringEnumConverter globalmente (Program.cs), e é
        // por isso que o corpo do POST chega correto. Mas o blob é gravado por
        // Serialize() e lido por Parse(), que usam ESTAS options — sem o
        // converter, o banco guardava "Mode": 1 e o Site, que compara com
        // "Shadow", renderizava o campo VAZIO ao reabrir a tela.
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Parse tolerante (leitura de dados antigos/corrompidos): nunca lança.
    /// Um blob inválido resulta em política vazia, que HERDA tudo do nível acima.
    /// </summary>
    /// <summary>
    /// Converte a primeira letra para minúscula (PascalCase -> camelCase).
    /// Evita a alocação/custo de uma naming policy no caminho da ingestão.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static AutoTicketPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AutoTicketPolicy();
        try
        {
            return JsonSerializer.Deserialize<AutoTicketPolicy>(json, Options) ?? new AutoTicketPolicy();
        }
        catch (JsonException)
        {
            // Corrompido: herda do nível superior em vez de derrubar a ingestão.
            return new AutoTicketPolicy();
        }
    }

    /// <summary>
    /// Serializa omitindo nulos, para que o blob guarde apenas o que aquele
    /// nível realmente sobrescreve (mantém a herança legível no banco).
    /// </summary>
    public string Serialize()
    {
        var map = new Dictionary<string, object?>();
        void Add(string key, object? value)
        {
            if (value is null) return;
            if (value is Array arr && arr.Length == 0) return;

            // Chave em camelCase: o dicionário é serializado com as MESMAS
            // options, mas a naming policy NÃO se aplica a chaves de dicionário.
            // Sem isto o blob saía "Enabled" enquanto o Site lê "enabled".
            map[ToCamelCase(key)] = value;
        }

        Add(nameof(Enabled), Enabled);
        Add(nameof(Mode), Mode);
        Add(nameof(CanaryClientIds), CanaryClientIds);
        Add(nameof(CanarySiteIds), CanarySiteIds);
        Add(nameof(MinConsecutiveFailures), MinConsecutiveFailures);
        Add(nameof(MinDurationSeconds), MinDurationSeconds);
        Add(nameof(MinImpactToOpen), MinImpactToOpen);
        Add(nameof(MinImpactToEscalate), MinImpactToEscalate);
        Add(nameof(OpenOnWarning), OpenOnWarning);
        Add(nameof(OpenOnPredictive), OpenOnPredictive);
        Add(nameof(MaintenanceGraceMinutes), MaintenanceGraceMinutes);
        Add(nameof(ReNotifyAfterMaintenanceEnds), ReNotifyAfterMaintenanceEnds);
        Add(nameof(RespectAgentMaintenance), RespectAgentMaintenance);
        Add(nameof(RespectMaintenanceWindow), RespectMaintenanceWindow);
        Add(nameof(SuppressWhenHostNotReachable), SuppressWhenHostNotReachable);
        Add(nameof(DependencySuppressionEnabled), DependencySuppressionEnabled);
        Add(nameof(DependencyMaxDepth), DependencyMaxDepth);
        Add(nameof(CloseTicketResolvedDuringMaintenance), CloseTicketResolvedDuringMaintenance);
        Add(nameof(FlapWindowMinutes), FlapWindowMinutes);
        Add(nameof(FlapTransitionsThreshold), FlapTransitionsThreshold);
        Add(nameof(DedupWindowMinutes), DedupWindowMinutes);
        Add(nameof(DedupWindowMaxHours), DedupWindowMaxHours);
        Add(nameof(ReopenWindowMinutes), ReopenWindowMinutes);
        Add(nameof(MaxCreatedTicketsPerHourPerAlertCode), MaxCreatedTicketsPerHourPerAlertCode);
        Add(nameof(IncrementOccurrenceCounter), IncrementOccurrenceCounter);
        Add(nameof(AllowedSensorKeys), AllowedSensorKeys);
        Add(nameof(NeverTicketSensorKeys), NeverTicketSensorKeys);
        Add(nameof(DefaultDepartmentId), DefaultDepartmentId);
        Add(nameof(DefaultCategory), DefaultCategory);
        Add(nameof(AiMode), AiMode);
        Add(nameof(AiMinConfidence), AiMinConfidence);
        Add(nameof(AiSuggestibleFields), AiSuggestibleFields);
        Add(nameof(AiContentGateFields), AiContentGateFields);
        Add(nameof(AiAllowEquipmentContext), AiAllowEquipmentContext);
        Add(nameof(AiRedactFields), AiRedactFields);
        Add(nameof(AiAnonymizeAssetNames), AiAnonymizeAssetNames);

        return JsonSerializer.Serialize(map, Options);
    }
}
