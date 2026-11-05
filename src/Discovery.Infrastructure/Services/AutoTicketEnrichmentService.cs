using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Enriquecimento por IA do chamado automático.
///
/// DESENHO DE SEGURANÇA (o que torna isto seguro de ligar):
///
///  1. A IA não decide nada. Quem decide abrir é o IAutoTicketSignalEvaluator,
///     determinístico. Aqui só se melhora o CONTEÚDO de um chamado já decidido.
///
///  2. Gate determinístico ANTES do modelo. Espelha o Unified Alert Grouping do
///     PagerDuty ("both conditions must be satisfied"): o conteúdo é conferido
///     contra campos de match exato antes de qualquer chamada ao LLM, de modo
///     que a IA nunca amplia o conjunto do que é considerado.
///
///  3. Sem tool calls. Usamos CompleteAsync, que NÃO suporta ferramentas
///     (EnableTools = false). A IA é fisicamente incapaz de executar ações.
///
///  4. Dados configuráveis. AiAllowEquipmentContext decide se o contexto do
///     equipamento sai da instalação; AiRedactFields remove PII/segredos; e
///     AiAnonymizeAssetNames pseudonimiza hosts/serviços.
///
///  5. Degradação garantida. Timeout, JSON inválido, orçamento estourado ou
///     circuito aberto -> AutoTicketEnrichment.Fallback, e o chamado nasce com o
///     texto determinístico. O enriquecimento NUNCA derruba a criação.
/// </summary>
public class AutoTicketEnrichmentService(
    DiscoveryDbContext db,
    ILlmProvider llmProvider,
    IConfigurationResolver configurationResolver,
    ILogger<AutoTicketEnrichmentService> logger) : IAutoTicketEnrichmentService
{
    private const double Temperature = 0.2;
    private const int DefaultMaxTokens = 1200;

    /// <summary>
    /// Teto do enriquecimento. O plano previa 15s: é o máximo que faz sentido
    /// segurar a ingestão de amostras esperando texto de IA.
    /// </summary>
    private const int MaxEnrichmentTimeoutMs = 15_000;
    private const int DefaultTimeoutMs = MaxEnrichmentTimeoutMs;

    /// <summary>
    /// Margem do CTS sobre o timeout do provider, para que o provider falhe
    /// primeiro com um erro específico em vez de ser cancelado por fora.
    /// </summary>
    private const int CtsGraceMs = 2_000;

    // ── Circuito ───────────────────────────────────────────────────────────
    // Após K falhas consecutivas, para de chamar o provedor por M minutos.
    private const int CircuitFailureThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(5);

    private static int _consecutiveFailures;
    private static DateTime _circuitOpenedAt = DateTime.MinValue;
    private static readonly Lock CircuitLock = new();

    public async Task<AutoTicketEnrichment> EnrichAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        CancellationToken cancellationToken = default)
    {
        // ── Pré-condições: qualquer uma basta para cair no determinístico ──
        if (policy.AiMode == AutoTicketAiMode.Off)
            return AutoTicketEnrichment.Fallback("AI mode is off.");

        if (IsCircuitOpen())
        {
            logger.LogDebug("Circuito de IA do auto-ticket aberto; usando texto determinístico.");
            return AutoTicketEnrichment.Fallback("Circuit breaker is open.");
        }

        var settings = await configurationResolver.GetAISettingsAsync();
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey))
            return AutoTicketEnrichment.Fallback("AI integration is not configured.");

        // ── GATE DETERMINÍSTICO (antes de qualquer LLM) ────────────────────
        // Só prossegue se o payload satisfaz os campos de match exato exigidos.
        // É o que impede a IA de "inventar" contexto para um evento que a regra
        // não autorizou.
        if (!PassesContentGate(monitoringEvent, policy, out var gateReason))
        {
            logger.LogDebug("Gate de conteúdo reprovou o enriquecimento: {Reason}", gateReason);
            return AutoTicketEnrichment.Fallback($"Content gate rejected: {gateReason}");
        }

        try
        {
            var context = await CollectContextAsync(monitoringEvent, policy, cancellationToken);
            var prompt = BuildPrompt(monitoringEvent, policy, context);

            // O enriquecimento roda INLINE no caminho da ingestão de sensores
            // (SensorIngestionService aguarda EvaluateAsync). Por isso o teto é
            // curto e NÃO herda o TimeoutMs global de IA (default 30s), que é
            // pensado para o chat.
            //
            // O deadline do CTS é MAIOR que o timeout do provider de propósito:
            // assim o provider tem chance de falhar primeiro com um erro
            // específico (HTTP, rate limit, etc.) e o CTS fica como rede de
            // segurança. Se os dois disparassem juntos, o cancelamento venceria
            // a corrida e todo erro viraria "timeout".
            var providerTimeoutMs = settings.TimeoutMs > 0
                ? Math.Min(settings.TimeoutMs, MaxEnrichmentTimeoutMs)
                : DefaultTimeoutMs;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(providerTimeoutMs + CtsGraceMs));

            var response = await llmProvider.CompleteAsync(
                SystemPrompt,
                [new LlmMessage("user", prompt)],
                new LlmOptions(
                    MaxTokens: DefaultMaxTokens,
                    Temperature: Temperature,
                    Model: settings.ChatModel,
                    BaseUrl: settings.BaseUrl,
                    ApiKey: settings.ApiKey,
                    // Explicitamente SEM tools: a IA não pode executar nada.
                    EnableTools: false,
                    Provider: settings.Provider,
                    // Atribuição do app nos logs do OpenRouter (provedor padrão).
                    OpenRouterReferer: settings.OpenRouterReferer,
                    OpenRouterTitle: settings.OpenRouterTitle,
                    OpenRouterCategories: settings.OpenRouterCategories,
                    TimeoutMs: providerTimeoutMs),
                cts.Token);

            var enrichment = ParseResponse(response, policy);

            if (enrichment.UsedFallback)
            {
                RegisterFailure();
                return enrichment;
            }

            RegisterSuccess();
            return enrichment;
        }
        catch (OperationCanceledException)
        {
            RegisterFailure();
            logger.LogWarning(
                "Timeout no enriquecimento por IA do evento {MonitoringEventId}; usando texto determinístico.",
                monitoringEvent.Id);
            return AutoTicketEnrichment.Fallback("Timeout calling the AI provider.");
        }
        catch (Exception ex)
        {
            RegisterFailure();
            logger.LogWarning(ex,
                "Falha no enriquecimento por IA do evento {MonitoringEventId}; usando texto determinístico.",
                monitoringEvent.Id);
            return AutoTicketEnrichment.Fallback("AI provider call failed.");
        }
    }

    // ── Gate determinístico ────────────────────────────────────────────────

    private static bool PassesContentGate(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        out string reason)
    {
        reason = string.Empty;

        // Sem campos configurados não há gate a aplicar: o evento já passou pelo
        // avaliador de sinal, que é a autoridade.
        if (policy.AiContentGateFields.Length == 0) return true;

        if (string.IsNullOrWhiteSpace(monitoringEvent.PayloadJson))
        {
            // Exigir match em payload ausente reprovaria todo evento sem payload.
            // Preferimos permitir e deixar o avaliador de sinal decidir.
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(monitoringEvent.PayloadJson);
            foreach (var field in policy.AiContentGateFields)
            {
                if (!doc.RootElement.TryGetProperty(field, out var value))
                {
                    reason = $"missing field '{field}'";
                    return false;
                }

                // Campo vazio NUNCA casa — a mesma regra que evita o acidente do
                // Zabbix, onde "a single empty tag value in both events is enough
                // to correlate them".
                if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                {
                    reason = $"field '{field}' is empty";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            reason = "payload is not valid JSON";
            return false;
        }
    }

    // ── Coleta de contexto (somente leitura) ───────────────────────────────

    private async Task<string> CollectContextAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Evento");
        sb.AppendLine($"AlertCode: {monitoringEvent.AlertCode}");
        sb.AppendLine($"Severity: {monitoringEvent.Severity}");
        sb.AppendLine($"Title: {monitoringEvent.Title}");
        sb.AppendLine($"Message: {Truncate(monitoringEvent.Message, 500)}");
        if (monitoringEvent.MetricKey is not null)
            sb.AppendLine($"Metric: {monitoringEvent.MetricKey} = {monitoringEvent.MetricValue}");
        sb.AppendLine($"OccurredAt (UTC): {monitoringEvent.OccurredAt:u}");

        // O envio de contexto do equipamento é PLUGÁVEL. Quando desligado, o
        // modelo recebe apenas o evento — sem identificação do ativo.
        if (!policy.AiAllowEquipmentContext)
        {
            sb.AppendLine();
            sb.AppendLine("## Contexto do equipamento");
            sb.AppendLine("(não autorizado pela configuração deste escopo)");
            return sb.ToString();
        }

        var agent = await db.Agents.AsNoTracking()
            .Where(a => a.Id == monitoringEvent.AgentId)
            .Select(a => new { a.Hostname, a.OperatingSystem, a.Status })
            .FirstOrDefaultAsync(ct);

        if (agent is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Equipamento");
            sb.AppendLine($"Host: {MaskAsset(agent.Hostname, policy)}");
            sb.AppendLine($"OS: {agent.OperatingSystem}");
        }

        // Definição do sensor: dá ao modelo o limiar e o alvo, indispensáveis
        // para uma descrição útil.
        if (monitoringEvent.SourceRefId is not null)
        {
            var sensor = await db.SensorDefinitions.AsNoTracking()
                .Where(s => s.Id == monitoringEvent.SourceRefId.Value)
                .Select(s => new
                {
                    s.Name,
                    s.Key,
                    s.Target,
                    s.WarningThreshold,
                    s.CriticalThreshold,
                    s.ThresholdUnit,
                    s.Comparison
                })
                .FirstOrDefaultAsync(ct);

            if (sensor is not null)
            {
                sb.AppendLine();
                sb.AppendLine("## Sensor");
                sb.AppendLine($"Name: {sensor.Name}");
                sb.AppendLine($"Key: {sensor.Key}");
                sb.AppendLine($"Target: {sensor.Target}");
                sb.AppendLine($"Thresholds: warning={sensor.WarningThreshold}, critical={sensor.CriticalThreshold} ({sensor.ThresholdUnit})");
                sb.AppendLine($"Comparison: {sensor.Comparison}");
            }
        }

        // Valores recentes: dá noção de tendência, não só do instante. Usamos o
        // estado corrente, que já carrega o valor primário e a série curta
        // (sparkline) — evita varrer a tabela de amostras brutas.
        if (monitoringEvent.SourceRefId is not null)
        {
            var state = await db.AgentSensorStates.AsNoTracking()
                .Where(s => s.AgentId == monitoringEvent.AgentId
                            && s.SensorId == monitoringEvent.SourceRefId.Value)
                .Select(s => new { s.PrimaryValue, s.PrimaryChannel, s.SparklineJson, s.ConsecutiveFailures })
                .FirstOrDefaultAsync(ct);

            if (state is not null)
            {
                sb.AppendLine();
                sb.AppendLine("## Estado atual do sensor");
                sb.AppendLine($"Channel: {state.PrimaryChannel} = {state.PrimaryValue}");
                sb.AppendLine($"ConsecutiveFailures: {state.ConsecutiveFailures}");
                if (!string.IsNullOrWhiteSpace(state.SparklineJson))
                    sb.AppendLine($"Recent series: {Truncate(state.SparklineJson, 300)}");
            }
        }

        // Departamentos válidos do cliente: o modelo só pode escolher entre eles.
        var departments = await db.Departments.AsNoTracking()
            .Where(d => d.ClientId == null || d.ClientId == monitoringEvent.ClientId)
            .Select(d => new { d.Id, d.Name })
            .Take(20)
            .ToListAsync(ct);

        if (departments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Departamentos válidos (escolha APENAS um destes ids)");
            foreach (var d in departments)
                sb.AppendLine($"- {d.Id}: {d.Name}");
        }

        // Payload, com redação de PII/segredos antes de sair da instalação.
        if (!string.IsNullOrWhiteSpace(monitoringEvent.PayloadJson))
        {
            sb.AppendLine();
            sb.AppendLine("## Payload (redigido)");
            sb.AppendLine(Redact(monitoringEvent.PayloadJson, policy));
        }

        return sb.ToString();
    }

    // ── Redação e pseudonimização ──────────────────────────────────────────

    /// <summary>
    /// Remove valores cujas chaves batem com AiRedactFields. É a barreira que
    /// impede segredos/PII de chegarem ao provedor.
    /// </summary>
    private static string Redact(string json, ResolvedAutoTicketPolicy policy)
    {
        if (policy.AiRedactFields.Length == 0) return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteRedacted(doc.RootElement, writer, policy.AiRedactFields);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            // Payload não-JSON: melhor não enviar do que arriscar vazar algo.
            return "(payload omitido: não é JSON válido)";
        }
    }

    private static void WriteRedacted(JsonElement element, Utf8JsonWriter writer, string[] redactFields)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var shouldRedact = redactFields.Any(f =>
                        property.Name.Contains(f, StringComparison.OrdinalIgnoreCase));

                    writer.WritePropertyName(property.Name);
                    if (shouldRedact)
                        writer.WriteStringValue("[REDACTED]");
                    else
                        WriteRedacted(property.Value, writer, redactFields);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedacted(item, writer, redactFields);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Pseudonimiza o nome do ativo quando o cliente não permite identificá-lo
    /// no provedor de IA. Mantém estabilidade (mesmo host = mesmo rótulo) para
    /// que o modelo ainda consiga raciocinar sobre o equipamento.
    /// </summary>
    private static string MaskAsset(string? hostname, ResolvedAutoTicketPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return "(desconhecido)";
        if (!policy.AiAnonymizeAssetNames) return hostname;

        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(hostname));
        return "asset-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    // ── Prompt e parsing ───────────────────────────────────────────────────

    private const string SystemPrompt = """
        Você é um assistente de operações de TI que redige chamados de suporte.

        REGRAS ABSOLUTAS:
        - Você NÃO decide se o chamado deve existir. Ele já foi decidido por um motor determinístico.
        - Você NÃO executa ações. Apenas redige texto e sugere campos.
        - Use SOMENTE os dados fornecidos. NUNCA invente métricas, valores, causas confirmadas ou histórico.
        - Hipóteses devem ser marcadas como hipóteses, nunca afirmadas como fato.
        - Responda APENAS com JSON válido, sem texto ao redor, no formato:
        {
          "title": "título curto e específico (máx 200 caracteres)",
          "description": "descrição em markdown com o que foi detectado, evidências e próximos passos",
          "evidence": ["fato objetivo 1", "fato objetivo 2"],
          "hypotheses": ["hipótese 1", "hipótese 2"],
          "priority": "Low|Medium|High|Critical",
          "departmentId": "guid de um dos departamentos listados, ou null",
          "category": "categoria curta, ou null",
          "confidence": 0.0
        }
        """;

    private static string BuildPrompt(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Redija o chamado para o evento abaixo.");
        sb.AppendLine();

        if (policy.SuggestibleFieldsSafe().Count > 0)
        {
            sb.AppendLine($"Campos que você pode sugerir: {string.Join(", ", policy.SuggestibleFieldsSafe())}.");
        }

        sb.AppendLine();
        sb.AppendLine(context);
        return sb.ToString();
    }

    private AutoTicketEnrichment ParseResponse(LlmResponse response, ResolvedAutoTicketPolicy policy)
    {
        var json = ExtractJson(response.Content);
        if (json is null)
            return AutoTicketEnrichment.Fallback("AI response was not valid JSON.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = GetString(root, "title");
            var description = GetString(root, "description");
            var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv)
                ? Math.Clamp(cv, 0, 1)
                : 0.5;

            var evidence = GetStringArray(root, "evidence");
            var hypotheses = GetStringArray(root, "hypotheses");

            var suggestions = new List<AutoTicketSuggestionItem>();

            // Só emitimos sugestões para campos que a política autoriza.
            var allowed = policy.SuggestibleFieldsSafe();

            if (allowed.Contains(AutoTicketSuggestionField.Priority.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var priority = GetString(root, "priority");
                if (!string.IsNullOrWhiteSpace(priority))
                {
                    suggestions.Add(new AutoTicketSuggestionItem
                    {
                        Field = AutoTicketSuggestionField.Priority,
                        SuggestedValue = priority,
                        Confidence = confidence,
                        Rationale = "Suggested by the AI from the event context."
                    });
                }
            }

            if (allowed.Contains(AutoTicketSuggestionField.Department.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var departmentId = GetString(root, "departmentId");
                if (!string.IsNullOrWhiteSpace(departmentId))
                {
                    suggestions.Add(new AutoTicketSuggestionItem
                    {
                        Field = AutoTicketSuggestionField.Department,
                        SuggestedValue = departmentId,
                        Confidence = confidence,
                        Rationale = "Suggested by the AI from the event context."
                    });
                }
            }

            if (allowed.Contains(AutoTicketSuggestionField.Category.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                var category = GetString(root, "category");
                if (!string.IsNullOrWhiteSpace(category))
                {
                    suggestions.Add(new AutoTicketSuggestionItem
                    {
                        Field = AutoTicketSuggestionField.Category,
                        SuggestedValue = category,
                        Confidence = confidence,
                        Rationale = "Suggested by the AI from the event context."
                    });
                }
            }

            return new AutoTicketEnrichment
            {
                Title = Truncate(title, 200),
                Description = description,
                Evidence = evidence,
                Hypotheses = hypotheses,
                Suggestions = suggestions,
                Confidence = confidence,
                Model = response.ModelVersion,
                TokensUsed = response.TokensUsed,
                UsedFallback = false
            };
        }
        catch (JsonException)
        {
            logger.LogWarning("Resposta da IA não pôde ser parseada como JSON; usando texto determinístico.");
            return AutoTicketEnrichment.Fallback("AI response was not parseable.");
        }
    }

    /// <summary>
    /// Extrai o objeto JSON da resposta, tolerando cercas markdown que o modelo
    /// às vezes adiciona apesar da instrução.
    /// </summary>
    private static string? ExtractJson(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = content.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        return text[start..(end + 1)];
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> GetStringArray(JsonElement root, string property)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
        }
        return result;
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? value
           : value.Length <= max ? value
           : value[..max];

    // ── Circuito ───────────────────────────────────────────────────────────

    private static bool IsCircuitOpen()
    {
        lock (CircuitLock)
        {
            if (_consecutiveFailures < CircuitFailureThreshold) return false;
            if (DateTime.UtcNow - _circuitOpenedAt < CircuitOpenDuration) return true;

            // Janela expirou: meio-aberto, deixa tentar de novo.
            _consecutiveFailures = 0;
            return false;
        }
    }

    private static void RegisterFailure()
    {
        lock (CircuitLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= CircuitFailureThreshold)
                _circuitOpenedAt = DateTime.UtcNow;
        }
    }

    private static void RegisterSuccess()
    {
        lock (CircuitLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenedAt = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Fecha o circuito imediatamente.
    ///
    /// Dois usos legítimos:
    ///  - Testes: o circuito é <c>static</c> (estado de processo), então testes de
    ///    falha o abrem e contaminariam os testes de caminho feliz.
    ///  - Operação: se o provedor voltar, não é preciso esperar a janela expirar.
    /// </summary>
    public static void ResetCircuit()
    {
        lock (CircuitLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenedAt = DateTime.MinValue;
        }
    }
}
