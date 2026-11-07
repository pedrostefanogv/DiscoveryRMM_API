using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Infrastructure.Services;

/// <summary>Constantes do domínio de sensores (mantidas num ponto só).</summary>
public static class SensorDefaults
{
    /// <summary>Intervalo default quando o sensor/infra não especifica outro.</summary>
    public const int DefaultIntervalSeconds = 300;
    public const int MinIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 86400;

    /// <summary>Sem resultado há mais de N× o intervalo => "sem dados recentes".</summary>
    public const int StaleMultiplier = 3;

    public const int MaxSparklinePoints = 30;
    public const int MaxOutputExcerpt = 2048;
    public const int MaxBatchSamples = 100;

    /// <summary>Intervalo efetivo de um sensor (0 = herda o default).</summary>
    public static int EffectiveInterval(SensorDefinition definition)
        => definition.IntervalSeconds > 0
            ? Math.Clamp(definition.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds)
            : DefaultIntervalSeconds;
}

/// <summary>Leitura/escrita do JSON de canais, tags, extratores e exit codes.</summary>
public static class SensorJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        // O JSON persistido pode vir do DTO (enums como string, ex.: "Count") ou
        // de payload antigo (enum numérico). Sem o converter, uma string derruba
        // a desserialização inteira e os canais "desaparecem" silenciosamente.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static IReadOnlyList<SensorChannelDto> ParseChannels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SensorChannelDto>>(json, Options) ?? [];
            return parsed
                .Where(c => !string.IsNullOrWhiteSpace(c.Key))
                .Select(c => new SensorChannelDto
                {
                    Key = c.Key.Trim(),
                    Label = string.IsNullOrWhiteSpace(c.Label) ? c.Key.Trim() : c.Label,
                    Unit = c.Unit,
                    Display = string.IsNullOrWhiteSpace(c.Display) ? "chart" : c.Display,
                    IsPrimary = c.IsPrimary
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json, Options) ?? [];
            return parsed.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteTags(IReadOnlyList<string>? tags)
        => tags is null ? "[]" : JsonSerializer.Serialize(tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), Options);

    public static string WriteChannels(IReadOnlyList<SensorChannelDto>? channels)
        => JsonSerializer.Serialize(channels ?? [], Options);

    public static Dictionary<string, double> ParseChannelsValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(json, Options);
            return parsed is null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? WriteChannelsValues(IReadOnlyDictionary<string, double>? values)
        => values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values, Options);

    public static Dictionary<string, List<double>> ParseSparkline(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<double>>>(json, Options);
            return parsed is null
                ? new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<double>>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? WriteSparkline(IReadOnlyDictionary<string, List<double>>? values)
        => values is null || values.Count == 0 ? null : JsonSerializer.Serialize(values, Options);
}

/// <summary>Opções específicas por tipo (options_json).</summary>
public sealed class SensorOptions
{
    private readonly JsonElement _root;
    private readonly bool _hasRoot;

    private SensorOptions(JsonElement root, bool hasRoot)
    {
        _root = root;
        _hasRoot = hasRoot;
    }

    public static SensorOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SensorOptions(default, false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new SensorOptions(default, false);
            return new SensorOptions(doc.RootElement.Clone(), true);
        }
        catch (JsonException)
        {
            return new SensorOptions(default, false);
        }
    }

    public bool GetBool(string name, bool fallback)
        => _hasRoot && _root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public int GetInt(string name, int fallback)
        => _hasRoot && _root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    public string? GetString(string name)
        => _hasRoot && _root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Regra de mapeamento de exit code (exit_code_map_json).</summary>
public sealed record SensorExitRule(
    IReadOnlyList<int> Codes,
    SensorStatus Status,
    SensorSeverity? Severity,
    string? Message);

public sealed class SensorExitRules
{
    public IReadOnlyList<SensorExitRule> Rules { get; init; } = [];
    public bool FailOnUnlistedNonZero { get; init; } = true;
    public SensorStatus UnlistedStatus { get; init; } = SensorStatus.Failing;
    public SensorSeverity? UnlistedSeverity { get; init; } = SensorSeverity.Critical;

    public static SensorExitRules Parse(string? json)
    {
        var defaults = new SensorExitRules
        {
            Rules =
            [
                new SensorExitRule([0], SensorStatus.Passing, null, null)
            ]
        };

        if (string.IsNullOrWhiteSpace(json)) return defaults;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return defaults;

            var rules = new List<SensorExitRule>();
            var failOnUnlisted = true;
            var unlistedStatus = SensorStatus.Failing;
            SensorSeverity? unlistedSeverity = SensorSeverity.Critical;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (item.TryGetProperty("unlistedNonZero", out var unlisted) && unlisted.ValueKind == JsonValueKind.Object)
                {
                    failOnUnlisted = true;
                    unlistedStatus = ParseStatus(unlisted, "status") ?? SensorStatus.Failing;
                    unlistedSeverity = ParseSeverity(unlisted, "severity") ?? SensorSeverity.Critical;
                    continue;
                }

                var codes = new List<int>();
                if (item.TryGetProperty("codes", out var codesElement) && codesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var code in codesElement.EnumerateArray())
                    {
                        if (code.TryGetInt32(out var parsedCode)) codes.Add(parsedCode);
                    }
                }

                var status = ParseStatus(item, "status") ?? SensorStatus.Failing;
                var severity = ParseSeverity(item, "severity");
                string? message = item.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
                    ? msg.GetString()
                    : null;

                rules.Add(new SensorExitRule(codes, status, severity, message));
            }

            return new SensorExitRules
            {
                Rules = rules.Count > 0 ? rules : defaults.Rules,
                FailOnUnlistedNonZero = failOnUnlisted,
                UnlistedStatus = unlistedStatus,
                UnlistedSeverity = unlistedSeverity
            };
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    private static SensorStatus? ParseStatus(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && Enum.TryParse<SensorStatus>(value.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : null;

    private static SensorSeverity? ParseSeverity(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && Enum.TryParse<SensorSeverity>(value.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// Catálogo dos canais numéricos de cada tipo built-in. Fonte única para a UI
/// (rótulo/unidade do valor e do gráfico) — sem isso o painel não saberia a
/// unidade de um canal derivado no servidor (ex.: "open" é booleano, não 1).
/// </summary>
public static class SensorChannelCatalog
{
    private static readonly IReadOnlyDictionary<SensorType, IReadOnlyList<SensorChannelDto>> ByType =
        new Dictionary<SensorType, IReadOnlyList<SensorChannelDto>>
        {
            [SensorType.DiskSpace] =
            [
                Channel("free_percent", "Livre", SensorChannelUnit.Percent, primary: true),
                Channel("free_bytes", "Livre (bytes)", SensorChannelUnit.Bytes),
                Channel("used_percent", "Usado", SensorChannelUnit.Percent),
                // O extrator já emitia total_bytes; sem entrada aqui o gráfico ficava sem rótulo.
                Channel("total_bytes", "Total (bytes)", SensorChannelUnit.Bytes)
            ],
            [SensorType.Cpu] = [Channel("used_percent", "Uso de CPU", SensorChannelUnit.Percent, primary: true)],
            [SensorType.Memory] =
            [
                Channel("used_percent", "Uso de memória", SensorChannelUnit.Percent, primary: true),
                Channel("available_bytes", "Disponível", SensorChannelUnit.Bytes),
                // Idem DiskSpace: emitido pelo extrator, faltava o rótulo.
                Channel("total_bytes", "Total (bytes)", SensorChannelUnit.Bytes)
            ],
            [SensorType.Uptime] = [Channel("uptime_seconds", "Tempo ligado", SensorChannelUnit.Seconds, primary: true)],
            [SensorType.Service] = [Channel("running", "Em execução", SensorChannelUnit.Boolean, primary: true)],
            [SensorType.Process] =
            [
                Channel("running", "Em execução", SensorChannelUnit.Boolean, primary: true),
                Channel("count", "Instâncias", SensorChannelUnit.Count),
                Channel("working_set_bytes", "Memória", SensorChannelUnit.Bytes)
            ],
            [SensorType.Ping] =
            [
                Channel("latency_avg_ms", "Latência média", SensorChannelUnit.Milliseconds, primary: true),
                Channel("latency_min_ms", "Latência mínima", SensorChannelUnit.Milliseconds),
                Channel("latency_max_ms", "Latência máxima", SensorChannelUnit.Milliseconds),
                Channel("jitter_ms", "Jitter", SensorChannelUnit.Milliseconds),
                Channel("loss_percent", "Perda", SensorChannelUnit.Percent)
            ],
            [SensorType.TcpPort] =
            [
                Channel("open", "Aberta", SensorChannelUnit.Boolean, primary: true),
                Channel("connect_ms", "Tempo de conexão", SensorChannelUnit.Milliseconds)
            ],
            [SensorType.HttpProbe] =
            [
                Channel("total_ms", "Tempo total", SensorChannelUnit.Milliseconds, primary: true),
                Channel("status_code", "Status HTTP", SensorChannelUnit.Count),
                Channel("dns_ms", "DNS", SensorChannelUnit.Milliseconds),
                Channel("connect_ms", "Conexão", SensorChannelUnit.Milliseconds),
                Channel("tls_ms", "TLS", SensorChannelUnit.Milliseconds),
                Channel("ttfb_ms", "TTFB", SensorChannelUnit.Milliseconds),
                Channel("bytes", "Tamanho", SensorChannelUnit.Bytes)
            ],
            [SensorType.HttpThroughput] =
            [
                Channel("download_mbps", "Download", SensorChannelUnit.MegabitsPerSecond, primary: true),
                Channel("upload_mbps", "Upload", SensorChannelUnit.MegabitsPerSecond),
                Channel("latency_ms", "Latência", SensorChannelUnit.Milliseconds)
            ],
            [SensorType.DnsLatency] = [Channel("resolve_ms", "Resolução", SensorChannelUnit.Milliseconds, primary: true)],
            [SensorType.Bandwidth] =
            [
                Channel("rx_bps", "Recebido", SensorChannelUnit.BytesPerSecond, primary: true),
                Channel("tx_bps", "Enviado", SensorChannelUnit.BytesPerSecond)
            ],
            [SensorType.DiskIo] =
            [
                Channel("busy_percent", "Ocupado", SensorChannelUnit.Percent, primary: true),
                Channel("read_bps", "Leitura", SensorChannelUnit.BytesPerSecond),
                Channel("write_bps", "Escrita", SensorChannelUnit.BytesPerSecond),
                Channel("latency_ms", "Latência", SensorChannelUnit.Milliseconds)
            ],
            [SensorType.EventLogRate] =
            [
                Channel("event_count", "Eventos", SensorChannelUnit.Count, primary: true),
                Channel("window_minutes", "Janela da consulta", SensorChannelUnit.Count)
            ],
            [SensorType.SmartStatus] =
            [
                Channel("smart_ok", "SMART saudável", SensorChannelUnit.Boolean, primary: true),
                Channel("temperature_c", "Temperatura", SensorChannelUnit.Celsius),
                Channel("reallocated_sectors", "Setores realocados", SensorChannelUnit.Count),
                Channel("power_on_hours", "Horas ligado", SensorChannelUnit.Count)
            ],
            [SensorType.CertificateExpiry] =
            [
                Channel("days_to_expiry", "Dias para expirar", SensorChannelUnit.Days, primary: true)
            ],
            [SensorType.DefenderStatus] =
            [
                Channel("defender_ok", "Antivírus ativo", SensorChannelUnit.Boolean, primary: true),
                Channel("signature_age_days", "Idade da assinatura", SensorChannelUnit.Days)
            ],
            [SensorType.BitLocker] =
            [
                Channel("bitlocker_on", "Volume protegido", SensorChannelUnit.Boolean, primary: true),
                Channel("encryption_percent", "Criptografia", SensorChannelUnit.Percent)
            ],
            // Consistência de AD. Os canais vêm do PRÓPRIO agente (o
            // SensorMeasure não tem campo para AD), por isso o primário é o
            // contador de falhas de replicação: é o mais acionável.
            [SensorType.ADConsistency] =
            [
                Channel("replication_failures", "Falhas de replicação", SensorChannelUnit.Count, primary: true),
                Channel("replication_ok", "Replicação saudável", SensorChannelUnit.Boolean),
                Channel("dcdiag_failed_tests", "Testes do dcdiag com falha", SensorChannelUnit.Count),
                Channel("dcdiag_ok", "dcdiag saudável", SensorChannelUnit.Boolean),
                Channel("time_offset_seconds", "Desvio de relógio", SensorChannelUnit.Seconds),
                Channel("clock_ok", "Relógio sincronizado", SensorChannelUnit.Boolean),
                // ad_collect_ok distingue "sem falhas" de "não conseguimos
                // perguntar" — é o canal que sustenta o Unknown.
                Channel("ad_collect_ok", "Coleta do AD concluída", SensorChannelUnit.Boolean)
            ]
        };

    private static SensorChannelDto Channel(string key, string label, SensorChannelUnit unit, bool primary = false)
        => new()
        {
            Key = key,
            Label = label,
            Unit = unit,
            Display = "chart",
            IsPrimary = primary
        };

    /// <summary>
    /// Canais efetivos do sensor: os declarados (Script) ou o catálogo do tipo.
    /// </summary>
    public static IReadOnlyList<SensorChannelDto> Resolve(SensorDefinition definition)
    {
        var declared = SensorJson.ParseChannels(definition.ChannelsJson);
        if (declared.Count > 0) return declared;
        return ByType.TryGetValue(definition.SensorType, out var channels) ? channels : [];
    }
}

/// <summary>Resultado da avaliação de um sensor (servidor autoritativo).</summary>
public sealed record SensorEvaluation(
    SensorStatus Status,
    SensorSeverity? Severity,
    string Message,
    string? DeclaredStatus,
    IReadOnlyDictionary<string, double> Channels,
    string? PrimaryChannel,
    double? PrimaryValue);

/// <summary>
/// Avaliação de sensores e derivação de canais. Ponto ÚNICO de decisão de
/// status/severidade — o agente envia apenas evidência.
/// </summary>
public static class SensorEvaluator
{
    public static SensorEvaluation Evaluate(
        SensorDefinition definition,
        AgentSensorMeasureDto measure,
        string? error,
        string? declaredStatus,
        string? declaredMessage,
        IReadOnlyDictionary<string, double>? providedChannels,
        string? targetKey = null)
    {
        var channels = DeriveChannels(definition, measure, providedChannels);
        var primaryChannel = PrimaryChannel(definition);
        double? primaryValue = primaryChannel is not null && channels.TryGetValue(primaryChannel, out var value)
            ? value
            : null;

        // Timeout de coleta (ping/http/script): falha explícita, não "Unknown".
        if (measure.TimedOut)
        {
            return Build(SensorStatus.Failing, SensorSeverity.Critical,
                $"Tempo limite excedido ({definition.TimeoutSeconds}s).", declaredStatus, channels, primaryChannel, primaryValue);
        }

        if (definition.SensorType == SensorType.Script)
        {
            return EvaluateScript(definition, measure, error, declaredStatus, declaredMessage, channels, primaryChannel, primaryValue);
        }

        // Erro de coleta => Unknown (não conta falha; alerta só com AlertOnSensorError).
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Build(SensorStatus.Unknown, null, error!, declaredStatus, channels, primaryChannel, primaryValue);
        }

        switch (definition.SensorType)
        {
            case SensorType.DiskSpace:
            {
                var volume = !string.IsNullOrWhiteSpace(targetKey)
                    ? targetKey!.Trim()
                    : ResolveTargetLabel(definition, measure);

                var total = measure.TotalBytes ?? 0;
                if ((measure.FreePercent is null && total <= 0))
                {
                    return Build(SensorStatus.Failing, SensorSeverity.Critical,
                        $"Volume {volume} não encontrado.",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, primaryValue);
                // Mensagem com o VOLUME: um sensor "todos os volumes" gera uma
                // linha por unidade e sem o nome as linhas ficam indistinguíveis.
                return Build(status, severity,
                    $"{volume}: {FormatNumber(primaryValue)}% livre.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.Service:
            {
                var state = measure.ServiceState ?? (measure.Running == true ? "Running" : "Stopped");
                var options = SensorOptions.Parse(definition.OptionsJson);
                var passIfStartPending = options.GetBool("passIfStartPending", true);
                var passIfNotExist = options.GetBool("passIfNotExist", false);

                var passing = state.Equals("Running", StringComparison.OrdinalIgnoreCase)
                    || (passIfStartPending && state.Equals("StartPending", StringComparison.OrdinalIgnoreCase))
                    || (passIfNotExist && state.Equals("NotFound", StringComparison.OrdinalIgnoreCase));

                // restartIfStopped (opção do agente): a tentativa aparece na
                // mensagem; o status continua sendo o estado FINAL observado.
                var restartNote = measure.RestartAttempted == true
                    ? measure.RestartSucceeded == true
                        ? " · reinício automático aplicado"
                        : " · tentativa de reinício falhou"
                    : string.Empty;

                return Build(
                    passing ? SensorStatus.Passing : SensorStatus.Failing,
                    passing ? null : definition.SeverityWhenFailing,
                    passing
                        ? $"Serviço {definition.Target} em execução{restartNote}."
                        : $"Serviço {definition.Target}: {state}{restartNote}.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.Process:
            {
                var running = measure.Running == true;
                return Build(
                    running ? SensorStatus.Passing : SensorStatus.Failing,
                    running ? null : definition.SeverityWhenFailing,
                    running
                        ? $"Processo {definition.Target} em execução ({measure.ProcessCount ?? 1})."
                        : $"Processo {definition.Target} não está em execução.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.TcpPort:
            {
                var open = measure.Running == true || (measure.ServiceState?.Equals("open", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!open)
                {
                    return Build(SensorStatus.Failing, definition.SeverityWhenFailing,
                        $"Porta {definition.Target} fechada.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, measure.ConnectMs);
                return Build(status, severity,
                    $"Porta {definition.Target} aberta ({FormatNumber(measure.ConnectMs)} ms).",
                    declaredStatus, channels, primaryChannel, primaryValue ?? measure.ConnectMs);
            }

            case SensorType.HttpProbe:
            {
                var options = SensorOptions.Parse(definition.OptionsJson);
                var minStatus = options.GetInt("expectedStatusMin", 200);
                var maxStatus = options.GetInt("expectedStatusMax", 399);
                var code = measure.StatusCode ?? 0;
                if (code < minStatus || code > maxStatus)
                {
                    return Build(SensorStatus.Failing, definition.SeverityWhenFailing,
                        $"HTTP {code} em {definition.Target} (esperado {minStatus}-{maxStatus}).",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, measure.TotalMs);
                return Build(status, severity,
                    $"HTTP {code} em {FormatNumber(measure.TotalMs)} ms.",
                    declaredStatus, channels, primaryChannel, primaryValue ?? measure.TotalMs);
            }

            case SensorType.Ping:
            {
                var sent = measure.Sent ?? 0;
                var replies = measure.Replies ?? 0;
                if (sent > 0 && replies == 0)
                {
                    return Build(SensorStatus.Failing, definition.SeverityWhenFailing,
                        $"{definition.Target}: sem resposta ({sent} enviados).",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                if (measure.LossPercent is > 0)
                {
                    return Build(SensorStatus.Failing, definition.SeverityWhenFailing,
                        $"{definition.Target}: perda de {FormatNumber(measure.LossPercent)}%.",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                // Sem latência medida (nem enviados, nem recebidos) o sensor não
                // mediu nada: Unknown, não Passing. Ver o ramo default abaixo.
                if (measure.AvgRttMs is null && sent == 0 && replies == 0)
                {
                    return Build(SensorStatus.Unknown, null,
                        $"{definition.Target}: sem medição.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, measure.AvgRttMs);
                return Build(status, severity,
                    $"{definition.Target}: {FormatNumber(measure.AvgRttMs)} ms (mín {FormatNumber(measure.LatencyMinMs)}, máx {FormatNumber(measure.LatencyMaxMs)}, jitter {FormatNumber(measure.JitterMs)}).",
                    declaredStatus, channels, primaryChannel, primaryValue ?? measure.AvgRttMs);
            }

            case SensorType.EventLogRate:
            {
                // Contagem ausente (consulta não retornou nada) é Unknown, não
                // "0 eventos" — zero medido é um resultado legítimo e continua
                // passando pelos limiares normalmente.
                if (measure.EventCount is null)
                {
                    return Build(SensorStatus.Unknown, null,
                        "Contagem de eventos indisponível.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, measure.EventCount);
                var window = measure.EventWindowMinutes is int minutes and > 0 ? minutes : 60;
                var count = measure.EventCount ?? 0;
                var last = measure.LastEventAt is DateTime at
                    ? $" Último: {at.ToUniversalTime():yyyy-MM-dd HH:mm} UTC."
                    : string.Empty;
                return Build(status, severity,
                    $"{count} evento(s) em {window} min.{last}",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.SmartStatus:
            {
                var smart = (measure.SmartStatus ?? string.Empty).Trim();
                if (smart.Length == 0)
                {
                    return Build(SensorStatus.Unknown, null,
                        "SMART indisponível no disco alvo.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = smart.ToLowerInvariant() switch
                {
                    "healthy" or "ok" or "passed" => (SensorStatus.Passing, (SensorSeverity?)null),
                    "warning" => (SensorStatus.Warning, SensorSeverity.Warning),
                    "unhealthy" or "failed" or "fail" => (SensorStatus.Failing, definition.SeverityWhenFailing),
                    _ => (SensorStatus.Unknown, (SensorSeverity?)null)
                };

                // Temperatura é opcional: se houver limiar configurado e ela
                // piorar o estado SMART, o pior vence.
                if (measure.TemperatureC is double temperature
                    && (definition.WarningThreshold is not null || definition.CriticalThreshold is not null))
                {
                    var (tempStatus, tempSeverity) = ByThresholds(definition, temperature);
                    if (Rank(tempStatus) > Rank(status))
                    {
                        status = tempStatus;
                        severity = tempSeverity;
                    }
                }

                return Build(status, severity,
                    $"SMART {smart}{(measure.TemperatureC is double t ? $" · {FormatNumber(t)} °C" : string.Empty)}.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.CertificateExpiry:
            {
                if (measure.DaysToExpiry is null)
                {
                    return Build(SensorStatus.Unknown, null,
                        "Certificado não encontrado ou sem data de validade.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, measure.DaysToExpiry);
                var subject = string.IsNullOrWhiteSpace(measure.CertificateSubject)
                    ? definition.Target
                    : measure.CertificateSubject;
                return Build(status, severity,
                    $"Certificado {subject} expira em {FormatNumber(measure.DaysToExpiry)} dia(s).",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.DefenderStatus:
            {
                if (measure.DefenderEnabled is null)
                {
                    return Build(SensorStatus.Unknown, null,
                        "Estado do antivírus indisponível.", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var realTimeOk = measure.DefenderRealTimeEnabled != false;
                var (status, severity) = measure.DefenderEnabled == true && realTimeOk
                    ? (SensorStatus.Passing, (SensorSeverity?)null)
                    : (SensorStatus.Failing, definition.SeverityWhenFailing);

                // Assinatura velha é um aviso adicional (quando há limiar).
                if (measure.SignatureAgeDays is double signatureAge
                    && (definition.WarningThreshold is not null || definition.CriticalThreshold is not null))
                {
                    var (sigStatus, sigSeverity) = ByThresholds(definition, signatureAge);
                    if (Rank(sigStatus) > Rank(status))
                    {
                        status = sigStatus;
                        severity = sigSeverity;
                    }
                }

                var detail = measure.DefenderEnabled != true
                    ? "antivírus desabilitado"
                    : !realTimeOk
                        ? "proteção em tempo real desabilitada"
                        : "ativo";
                var signature = measure.SignatureAgeDays is double age
                    ? $" · assinatura com {FormatNumber(age)} dia(s)"
                    : string.Empty;

                return Build(status, severity, $"Antivírus: {detail}{signature}.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.BitLocker:
            {
                var state = (measure.BitLockerStatus ?? string.Empty).Trim();
                var (status, severity) = state.ToLowerInvariant() switch
                {
                    "on" or "protected" => (SensorStatus.Passing, (SensorSeverity?)null),
                    "suspended" => (SensorStatus.Warning, SensorSeverity.Warning),
                    "off" or "unprotected" => (SensorStatus.Failing, definition.SeverityWhenFailing),
                    _ => (SensorStatus.Unknown, (SensorSeverity?)null)
                };

                var percent = measure.BitLockerEncryptionPercent is double p
                    ? $" ({FormatNumber(p)}%)"
                    : string.Empty;
                var label = string.IsNullOrWhiteSpace(state) ? "estado desconhecido" : state;
                return Build(status, severity, $"BitLocker {label}{percent}.",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            case SensorType.ADConsistency:
            {
                // PRINCÍPIO: "não perguntamos" NÃO é "está tudo certo".
                //
                // ad_collect_ok=false significa que repadmin/dcdiag/w32tm não
                // responderam — o agente não conseguiu falar com o AD. Reportar
                // Passing aqui pintaria de verde um DC que nunca foi verificado.
                // É o mesmo princípio do isServer null: ausência de evidência
                // não é evidência de saúde. Unknown não conta falha consecutiva
                // e só alerta com AlertOnSensorError.
                double? collectOk = channels.TryGetValue("ad_collect_ok", out var collectFlag) ? collectFlag : null;

                if (collectOk is null)
                {
                    return Build(SensorStatus.Unknown, null,
                        "Coleta de consistência do AD não reportou se conseguiu consultar o domínio.",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                if (collectOk.Value < 1)
                {
                    // Coleta vazia por falta de resposta: o agente devolve isso
                    // em cliente/servidor membro (sensor não se aplica) e quando
                    // os três utilitários falham.
                    return Build(SensorStatus.Unknown, null,
                        "Coleta de consistência do AD não conseguiu consultar o domínio (repadmin/dcdiag/w32tm).",
                        declaredStatus, channels, primaryChannel, primaryValue);
                }

                // Daqui para baixo houve coleta de verdade: ausência de um
                // bloco passa a significar "aquele bloco falhou", não "não medido".
                var issues = new List<string>();
                var status = SensorStatus.Passing;
                SensorSeverity? severity = null;

                void Escalate(SensorStatus candidate, SensorSeverity? candidateSeverity)
                {
                    if (Rank(candidate) > Rank(status))
                    {
                        status = candidate;
                        severity = candidateSeverity;
                    }
                }

                // 1) Replicação: qualquer falha é problema real de AD.
                if (channels.TryGetValue("replication_failures", out var failures))
                {
                    if (failures > 0)
                    {
                        issues.Add($"{FormatNumber(failures)} falha(s) de replicação");
                        Escalate(SensorStatus.Failing, definition.SeverityWhenFailing);
                    }
                }
                else
                {
                    issues.Add("replicação não avaliada");
                    Escalate(SensorStatus.Warning, SensorSeverity.Warning);
                }

                // 2) dcdiag: um teste com falha já indica DC degradado.
                if (channels.TryGetValue("dcdiag_failed_tests", out var dcdiagFailures))
                {
                    if (dcdiagFailures > 0)
                    {
                        issues.Add($"{FormatNumber(dcdiagFailures)} teste(s) do dcdiag com falha");
                        Escalate(SensorStatus.Warning, SensorSeverity.Warning);
                    }
                }
                else
                {
                    issues.Add("dcdiag não avaliado");
                    Escalate(SensorStatus.Warning, SensorSeverity.Warning);
                }

                // 3) Relógio: desvio > 15s quebra Kerberos.
                if (channels.TryGetValue("clock_ok", out var clockOk))
                {
                    if (clockOk < 1)
                    {
                        var offset = channels.TryGetValue("time_offset_seconds", out var o)
                            ? $" (desvio de {FormatNumber(o)} s)"
                            : string.Empty;
                        issues.Add("relógio fora de sincronia" + offset);
                        Escalate(SensorStatus.Warning, SensorSeverity.Warning);
                    }
                }
                else
                {
                    issues.Add("relógio não avaliado");
                    Escalate(SensorStatus.Warning, SensorSeverity.Warning);
                }

                return Build(status, severity,
                    issues.Count == 0
                        ? "Consistência do AD: replicação, dcdiag e relógio sem problemas."
                        : "Consistência do AD: " + string.Join("; ", issues) + ".",
                    declaredStatus, channels, primaryChannel, primaryValue);
            }

            default:
            {
                // Sem medição o sensor NÃO está saudável: o coletor não conseguiu
                // medir o canal primário (ex.: placa de rede ociosa, contador de
                // disco indisponível, disco sem SMART). Reportar Passing aqui
                // pintaria de verde um sensor que não mediu nada. É Unknown — que
                // não incrementa falhas consecutivas e só alerta com
                // AlertOnSensorError — e não Failing, porque a ausência pode ser
                // legítima. "Passing" fica reservado a quem realmente mediu e
                // ficou dentro do limiar.
                if (primaryValue is null)
                {
                    return Build(SensorStatus.Unknown, null,
                        $"{Describe(definition, null, null)} (sem medição).", declaredStatus, channels, primaryChannel, primaryValue);
                }

                var (status, severity) = ByThresholds(definition, primaryValue);
                return Build(status, severity,
                    Describe(definition, primaryValue, null), declaredStatus, channels, primaryChannel, primaryValue);
            }
        }
    }

    private static SensorEvaluation EvaluateScript(
        SensorDefinition definition,
        AgentSensorMeasureDto measure,
        string? error,
        string? declaredStatus,
        string? declaredMessage,
        IReadOnlyDictionary<string, double> channels,
        string? primaryChannel,
        double? primaryValue)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Build(SensorStatus.Unknown, null, error!, declaredStatus, channels, primaryChannel, primaryValue);
        }

        if (measure.TimedOut)
        {
            return Build(SensorStatus.Failing, SensorSeverity.Critical,
                $"Tempo limite excedido ({definition.TimeoutSeconds}s).", declaredStatus, channels, primaryChannel, primaryValue);
        }

        // 1) Status declarado pelo próprio script (MDZ_STATUS).
        if (!string.IsNullOrWhiteSpace(declaredStatus)
            && TryMapDeclaredStatus(declaredStatus, out var declaredMapped))
        {
            return Build(declaredMapped.Status, declaredMapped.Severity,
                !string.IsNullOrWhiteSpace(declaredMessage) ? declaredMessage! : Describe(definition, primaryValue, null),
                declaredStatus, channels, primaryChannel, primaryValue);
        }

        // 2) Exit code (mapa configurável).
        var rules = SensorExitRules.Parse(definition.ExitCodeMapJson);
        var exitCode = measure.ExitCode ?? -1;
        foreach (var rule in rules.Rules)
        {
            if (!rule.Codes.Contains(exitCode)) continue;
            return Build(rule.Status, rule.Severity,
                rule.Message ?? Describe(definition, primaryValue, null),
                declaredStatus, channels, primaryChannel, primaryValue);
        }

        var unlisted = exitCode switch
        {
            0 => new SensorExitRule([0], SensorStatus.Passing, null, null),
            _ => new SensorExitRule([exitCode], rules.FailOnUnlistedNonZero ? rules.UnlistedStatus : SensorStatus.Passing,
                rules.FailOnUnlistedNonZero ? rules.UnlistedSeverity : null, null)
        };

        return Build(unlisted.Status, unlisted.Severity,
            unlisted.Message ?? (measure.ExitCode is int code ? $"Script retornou código {code}." : Describe(definition, primaryValue, null)),
            declaredStatus, channels, primaryChannel, primaryValue);
    }

    private static bool TryMapDeclaredStatus(string raw, out (SensorStatus Status, SensorSeverity? Severity) mapped)
    {
        var normalized = raw.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "OK":
            case "PASSING":
            case "PASS":
                mapped = (SensorStatus.Passing, null);
                return true;
            case "WARNING":
            case "WARN":
                mapped = (SensorStatus.Warning, SensorSeverity.Warning);
                return true;
            case "CRITICAL":
            case "FAILING":
            case "FAIL":
                mapped = (SensorStatus.Failing, SensorSeverity.Critical);
                return true;
            case "UNKNOWN":
                mapped = (SensorStatus.Unknown, null);
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    /// <summary>Aplica os limiares ao valor do canal primário.</summary>
    public static (SensorStatus Status, SensorSeverity? Severity) ByThresholds(SensorDefinition definition, double? value)
    {
        if (value is not double current) return (SensorStatus.Passing, null);

        var lowerIsWorse = definition.Comparison == SensorComparison.LowerIsWorse;

        if (definition.CriticalThreshold is double critical
            && (lowerIsWorse ? current < critical : current > critical))
        {
            return (SensorStatus.Failing, SensorSeverity.Critical);
        }

        if (definition.WarningThreshold is double warning
            && (lowerIsWorse ? current < warning : current > warning))
        {
            return (SensorStatus.Warning, SensorSeverity.Warning);
        }

        return (SensorStatus.Passing, null);
    }

    /// <summary>
    /// Histerese de ESCALAÇÃO: mantém o estado atual quando a severidade vai
    /// subir mas ainda não há <c>SustainSamples</c> amostras ruins consecutivas.
    /// Não afeta a primeira falha (sensor que estava OK muda na hora) nem a
    /// recuperação (Passing é sempre imediato).
    /// </summary>
    public static bool ShouldHoldEscalation(
        SensorDefinition definition,
        SensorStatus previousStatus,
        SensorStatus nextStatus,
        int consecutiveFailuresBefore)
    {
        if (definition.SustainSamples <= 1) return false;
        if (previousStatus == SensorStatus.Passing) return false;
        if (Rank(nextStatus) <= Rank(previousStatus)) return false;

        // +1 = a amostra atual, que ainda não foi contabilizada no contador.
        return consecutiveFailuresBefore + 1 < definition.SustainSamples;
    }

    /// <summary>Ranking de gravidade do status (para "o pior vence").</summary>
    public static int Rank(SensorStatus status) => status switch
    {
        SensorStatus.Failing => 4,
        SensorStatus.Warning => 3,
        SensorStatus.Unknown => 2,
        SensorStatus.Passing => 1,
        _ => 0
    };

    public static string? PrimaryChannel(SensorDefinition definition)
    {
        // Canais declarados vencem sempre: o usuário pode apontar qual canal de um
        // tipo nativo é o primário (ex.: usar `used_percent` do disco no lugar do
        // `free_percent`). Sem declaração, cai no primário nativo do tipo.
        var declared = SensorJson.ParseChannels(definition.ChannelsJson);
        if (declared.FirstOrDefault(c => c.IsPrimary)?.Key is { Length: > 0 } explicitPrimary)
        {
            return explicitPrimary;
        }

        if (definition.SensorType == SensorType.Script)
        {
            return declared.FirstOrDefault()?.Key;
        }

        return definition.SensorType switch
        {
            SensorType.DiskSpace => "free_percent",
            SensorType.Cpu => "used_percent",
            SensorType.Memory => "used_percent",
            SensorType.Uptime => "uptime_seconds",
            SensorType.Service => "running",
            SensorType.Process => "running",
            SensorType.Ping => "latency_avg_ms",
            SensorType.TcpPort => "open",
            SensorType.HttpProbe => "total_ms",
            SensorType.HttpThroughput => "download_mbps",
            SensorType.DnsLatency => "resolve_ms",
            SensorType.Bandwidth => "rx_bps",
            SensorType.DiskIo => "busy_percent",
            SensorType.EventLogRate => "event_count",
            SensorType.SmartStatus => "smart_ok",
            SensorType.CertificateExpiry => "days_to_expiry",
            SensorType.DefenderStatus => "defender_ok",
            SensorType.BitLocker => "bitlocker_on",
            SensorType.ADConsistency => "replication_failures",
            _ => null
        };
    }

    /// <summary>Canais built-in derivados da evidência (Script usa os canais já extraídos).</summary>
    public static Dictionary<string, double> DeriveChannels(
        SensorDefinition definition,
        AgentSensorMeasureDto measure,
        IReadOnlyDictionary<string, double>? providedChannels)
    {
        var channels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        switch (definition.SensorType)
        {
            case SensorType.DiskSpace:
                Add(channels, "free_percent", measure.FreePercent);
                Add(channels, "free_bytes", measure.FreeBytes);
                Add(channels, "total_bytes", measure.TotalBytes);
                Add(channels, "used_percent", measure.UsedPercent);
                break;
            case SensorType.Cpu:
                Add(channels, "used_percent", measure.UsedPercent);
                break;
            case SensorType.Memory:
                Add(channels, "used_percent", measure.UsedPercent);
                Add(channels, "available_bytes", measure.FreeBytes);
                Add(channels, "total_bytes", measure.TotalBytes);
                break;
            case SensorType.Uptime:
                Add(channels, "uptime_seconds", measure.UptimeSeconds);
                break;
            case SensorType.Service:
                channels["running"] = measure.ServiceState?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true
                    || measure.Running == true ? 1 : 0;
                Add(channels, "start_pending", measure.ServiceState?.Equals("StartPending", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0);
                break;
            case SensorType.Process:
                channels["running"] = measure.Running == true ? 1 : 0;
                Add(channels, "count", measure.ProcessCount);
                Add(channels, "working_set_bytes", measure.WorkingSetBytes);
                break;
            case SensorType.Ping:
                Add(channels, "latency_avg_ms", measure.AvgRttMs);
                Add(channels, "latency_min_ms", measure.LatencyMinMs);
                Add(channels, "latency_max_ms", measure.LatencyMaxMs);
                Add(channels, "jitter_ms", measure.JitterMs);
                Add(channels, "loss_percent", measure.LossPercent);
                break;
            case SensorType.TcpPort:
                channels["open"] = measure.Running == true ? 1 : 0;
                Add(channels, "connect_ms", measure.ConnectMs);
                break;
            case SensorType.HttpProbe:
                Add(channels, "status_code", measure.StatusCode);
                Add(channels, "dns_ms", measure.DnsMs);
                Add(channels, "connect_ms", measure.ConnectMs);
                Add(channels, "tls_ms", measure.TlsMs);
                Add(channels, "ttfb_ms", measure.TtfbMs);
                Add(channels, "total_ms", measure.TotalMs);
                Add(channels, "bytes", measure.Bytes);
                break;
            case SensorType.HttpThroughput:
                Add(channels, "download_mbps", measure.DownloadMbps);
                Add(channels, "upload_mbps", measure.UploadMbps);
                Add(channels, "latency_ms", measure.AvgRttMs ?? measure.TotalMs);
                break;
            case SensorType.DnsLatency:
                Add(channels, "resolve_ms", measure.ResolveMs);
                break;
            case SensorType.Bandwidth:
                Add(channels, "rx_bps", measure.RxBps);
                Add(channels, "tx_bps", measure.TxBps);
                break;
            case SensorType.DiskIo:
                Add(channels, "read_bps", measure.ReadBps);
                Add(channels, "write_bps", measure.WriteBps);
                Add(channels, "busy_percent", measure.BusyPercent);
                Add(channels, "latency_ms", measure.DiskLatencyMs);
                break;
            case SensorType.EventLogRate:
                Add(channels, "event_count", measure.EventCount);
                Add(channels, "window_minutes", measure.EventWindowMinutes);
                break;
            case SensorType.SmartStatus:
                channels["smart_ok"] = string.IsNullOrWhiteSpace(measure.SmartStatus)
                    ? 0
                    : measure.SmartStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                Add(channels, "temperature_c", measure.TemperatureC);
                Add(channels, "reallocated_sectors", measure.ReallocatedSectors);
                Add(channels, "power_on_hours", measure.PowerOnHours);
                break;
            case SensorType.CertificateExpiry:
                Add(channels, "days_to_expiry", measure.DaysToExpiry);
                break;
            case SensorType.DefenderStatus:
                channels["defender_ok"] = measure.DefenderEnabled == true && measure.DefenderRealTimeEnabled != false ? 1 : 0;
                Add(channels, "signature_age_days", measure.SignatureAgeDays);
                break;
            case SensorType.BitLocker:
                channels["bitlocker_on"] = (measure.BitLockerStatus ?? string.Empty)
                    .Equals("On", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                Add(channels, "encryption_percent", measure.BitLockerEncryptionPercent);
                break;
            case SensorType.ADConsistency:
            {
                // Fonte preferida: campos explícitos da medida.
                Add(channels, "replication_failures", measure.AdReplicationFailures);
                Add(channels, "replication_ok", Bool01(measure.AdReplicationOk));
                Add(channels, "dcdiag_failed_tests", measure.AdDcdiagFailedTests);
                Add(channels, "dcdiag_ok", Bool01(measure.AdDcdiagOk));
                Add(channels, "time_offset_seconds", measure.AdTimeOffsetSeconds);
                Add(channels, "clock_ok", Bool01(measure.AdClockOk));
                Add(channels, "ad_collect_ok", Bool01(measure.AdCollectOk));

                // Fallback: o agente Go publica os MESMOS valores no mapa
                // Channels (é o transporte histórico deste tipo). Só preenche o
                // que ainda falta, para o campo explícito sempre vencer.
                if (providedChannels is not null)
                {
                    foreach (var key in AdChannelKeys)
                    {
                        if (channels.ContainsKey(key)) continue;
                        if (providedChannels.TryGetValue(key, out var value) && double.IsFinite(value))
                            channels[key] = value;
                    }
                }

                break;
            }
            case SensorType.Script:
            {
                var declared = SensorJson.ParseChannels(definition.ChannelsJson)
                    .Select(c => c.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (providedChannels is not null)
                {
                    foreach (var (key, value) in providedChannels)
                    {
                        // Canal não declarado é descartado: evita explosão de cardinalidade.
                        if (declared.Count > 0 && !declared.Contains(key)) continue;
                        if (double.IsFinite(value)) channels[key] = value;
                    }
                }

                break;
            }
        }

        return channels;
    }

    /// <summary>
    /// Canais do sensor de consistência de AD. CONTRATO com o agente Go:
    /// os nomes estão em sensors_ad_windows.go (ChannelAD*) e renomear
    /// qualquer um quebra o outro lado em silêncio.
    /// </summary>
    private static readonly string[] AdChannelKeys =
    [
        "ad_collect_ok",
        "replication_failures",
        "replication_ok",
        "dcdiag_failed_tests",
        "dcdiag_ok",
        "time_offset_seconds",
        "clock_ok"
    ];

    /// <summary>Converte bool opcional em 1/0 para canal numérico (null = ausente).</summary>
    private static double? Bool01(bool? value) => value is null ? null : value.Value ? 1d : 0d;

    private static void Add(Dictionary<string, double> channels, string key, double? value)
    {
        if (value is double actual && double.IsFinite(actual)) channels[key] = actual;
    }

    private static SensorEvaluation Build(
        SensorStatus status,
        SensorSeverity? severity,
        string message,
        string? declaredStatus,
        IReadOnlyDictionary<string, double> channels,
        string? primaryChannel,
        double? primaryValue)
        => new(status, severity, message, declaredStatus, channels, primaryChannel, primaryValue);

    private static string ResolveTargetLabel(SensorDefinition definition, AgentSensorMeasureDto measure)
        => string.IsNullOrWhiteSpace(definition.Target) ? "(alvo)" : definition.Target;

    private static string Describe(SensorDefinition definition, double? value, string? suffix)
    {
        if (value is not double current) return $"{definition.Name}: sem valor.";
        var unit = UnitSuffix(definition.ThresholdUnit);
        var tail = string.IsNullOrWhiteSpace(suffix) ? string.Empty : $" {suffix}";
        return $"{definition.Name}: {FormatNumber(current)}{unit}{tail}.";
    }

    public static string UnitSuffix(SensorChannelUnit unit) => unit switch
    {
        SensorChannelUnit.Percent => "%",
        SensorChannelUnit.Bytes => " B",
        SensorChannelUnit.BytesPerSecond => " B/s",
        SensorChannelUnit.Milliseconds => " ms",
        SensorChannelUnit.Seconds => " s",
        SensorChannelUnit.MegabitsPerSecond => " Mbps",
        SensorChannelUnit.Days => " d",
        SensorChannelUnit.Celsius => " °C",
        _ => string.Empty
    };

    public static string FormatNumber(double? value)
        => value is double actual
            ? actual.ToString(actual >= 100 ? "0" : "0.##", CultureInfo.InvariantCulture)
            : "—";
}

/// <summary>Cálculo do fingerprint da policy de sensores (mesma chave no sync e na UI).</summary>
public static class SensorFingerprint
{
    public static string Compute(
        IReadOnlyList<SensorDefinition> definitions,
        IReadOnlyDictionary<Guid, AutomationScriptDefinition> scriptsById)
    {
        var builder = new StringBuilder();
        foreach (var definition in definitions.OrderBy(d => d.Id))
        {
            builder.Append(definition.Id).Append('|')
                .Append(definition.LastUpdatedAt.ToUniversalTime().Ticks).Append('|')
                .Append((int)definition.SensorType).Append('|')
                .Append(definition.Target ?? string.Empty).Append('|')
                .Append(SensorDefaults.EffectiveInterval(definition)).Append('|')
                .Append(definition.TimeoutSeconds).Append('|')
                .Append(Hash(string.Concat(
                    definition.OptionsJson,
                    definition.ChannelsJson,
                    definition.ExtractorsJson,
                    definition.ExitCodeMapJson))).Append('|');

            if (definition.ScriptId is Guid scriptId && scriptsById.TryGetValue(scriptId, out var script))
            {
                builder.Append(script.Version).Append('|').Append(Hash(script.Content));
            }

            builder.Append('|').Append(definition.IsActive ? "1" : "0").Append(';');
        }

        return Hash(builder.ToString());
    }

    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
