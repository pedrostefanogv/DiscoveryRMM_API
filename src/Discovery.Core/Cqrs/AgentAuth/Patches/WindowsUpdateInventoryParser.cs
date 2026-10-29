using System.Globalization;
using System.Text.Json;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;

namespace Discovery.Core.Cqrs.AgentAuth.Patches;

/// <summary>
/// Normaliza o payload de inventário de Windows Update enviado pelo agente.
///
/// Regras de segurança: chaves canônicas validadas, KBs normalizados,
/// listas limitadas (1000 updates / 10 motivos) e campos truncados nos limites
/// das colunas. Um item inválido é descartado — nunca derruba o upload inteiro.
/// </summary>
public static class WindowsUpdateInventoryParser
{
    private const int MaxUpdates = 1000;
    private const int MaxTitle = 500;
    private const int MaxDescription = 2000;
    private const int MaxClassification = 80;
    private const int MaxSeverity = 40;
    private const int MaxSupportUrl = 1000;
    private const int MaxSource = 20;
    private const int MaxWsusServer = 500;
    private const int MaxCount = 100_000;

    private static readonly HashSet<string> ScanStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ok", "error", "timeout", "unsupported", "partial"
    };

    /// <summary>Disparos de scan aceitos no bloco "scan".</summary>
    private static readonly HashSet<string> ScanTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        "periodic", "change", "command", "install"
    };

    /// <summary>Origens do scan aceitas no bloco "scan".</summary>
    private static readonly HashSet<string> ScanSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "wua", "qfe"
    };

    /// <summary>Motivos de descarte aceitos em "skippedReasons".</summary>
    private static readonly HashSet<string> SkippedReasonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "driver", "no_identity", "unreadable"
    };

    /// <summary>Estados do serviço wuauserv aceitos no bloco "state".</summary>
    private static readonly HashSet<string> ServiceStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "running", "stopped", "disabled", "unknown"
    };

    private static readonly HashSet<string> RebootReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows_update", "cbs", "file_rename", "computer_rename", "servicing"
    };

    public static IReadOnlyList<AgentWindowsUpdateEntry> ParseUpdates(object? updates)
    {
        var items = ExtractArray(updates);
        if (items is null)
            return [];

        var result = new List<AgentWindowsUpdateEntry>();
        foreach (var item in items.Value.EnumerateArray().Take(MaxUpdates))
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var entry = MapEntry(item);
            if (entry is not null)
                result.Add(entry);
        }
        return result;
    }

    public static PatchScanMeta ParseScan(object? scan)
    {
        var meta = new PatchScanMeta { Status = "ok" };
        var element = ExtractObject(scan);
        if (element is null)
            return meta;

        var root = element.Value;
        var status = GetString(root, "status");
        meta.Status = !string.IsNullOrWhiteSpace(status) && ScanStatuses.Contains(status.Trim())
            ? status.Trim().ToLowerInvariant()
            : "ok";

        // "errorCode" é o nome canônico; "error" é aceito como alias para
        // agentes que enviem o bloco com o rótulo curto.
        var errorCode = GetString(root, "errorCode") ?? GetString(root, "error");
        if (!string.IsNullOrWhiteSpace(errorCode))
            meta.ErrorCode = Truncate(errorCode, 200);

        if (root.TryGetProperty("durationMs", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt32(out var duration) &&
            duration is >= 0 and <= 3_600_000)
        {
            meta.DurationMs = duration;
        }

        var startedAt = GetString(root, "startedAt");
        meta.StartedAt = ParseDate(startedAt);

        // Diagnóstico opcional: ausente em agentes antigos (null = preserva o
        // valor conhecido no snapshot).
        meta.SkippedCount = GetNullableInt(root, "skippedCount", 0, MaxCount);
        meta.SkippedReasons = ParseSkippedReasons(root);

        var trigger = GetString(root, "trigger");
        if (!string.IsNullOrWhiteSpace(trigger) && ScanTriggers.Contains(trigger.Trim()))
            meta.Trigger = trigger.Trim().ToLowerInvariant();

        var source = GetString(root, "source");
        if (!string.IsNullOrWhiteSpace(source) && ScanSources.Contains(source.Trim()))
            meta.Source = Truncate(source.Trim().ToLowerInvariant(), MaxSource);

        return meta;
    }

    /// <summary>
    /// Mapa motivo → quantidade do bloco "scan". Só chaves conhecidas
    /// (driver, no_identity, unreadable), no máximo 10, valores 0..100000.
    /// Retorna null quando o agente não manda o objeto (compatibilidade).
    /// </summary>
    private static IReadOnlyDictionary<string, int>? ParseSkippedReasons(JsonElement root)
    {
        if (!root.TryGetProperty("skippedReasons", out var reasonsElement) ||
            reasonsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in reasonsElement.EnumerateObject().Take(10))
        {
            var key = property.Name.Trim().ToLowerInvariant();
            if (!SkippedReasonKeys.Contains(key) || reasons.ContainsKey(key))
                continue;
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var value) ||
                value is <= 0 or > MaxCount)
            {
                continue;
            }
            reasons[key] = value;
        }

        return reasons.Count == 0 ? null : reasons;
    }

    public static PatchPendingRebootMeta? ParsePendingReboot(object? pendingReboot)
    {
        var element = ExtractObject(pendingReboot);
        if (element is null)
            return null;

        var root = element.Value;
        var pending = false;
        if (root.TryGetProperty("pending", out var pendingElement))
        {
            pending = pendingElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bool.TryParse(pendingElement.GetString(), out var parsed) && parsed,
                _ => false
            };
        }

        var reasons = new List<string>();
        if (root.TryGetProperty("reasons", out var reasonsElement) &&
            reasonsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var reasonElement in reasonsElement.EnumerateArray().Take(10))
            {
                if (reasonElement.ValueKind != JsonValueKind.String)
                    continue;
                var reason = reasonElement.GetString()?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(reason) &&
                    RebootReasons.Contains(reason) &&
                    !reasons.Contains(reason, StringComparer.Ordinal))
                {
                    reasons.Add(reason);
                }
            }
        }

        var checkedAt = ParseDate(GetString(root, "checkedAt"));
        return new PatchPendingRebootMeta
        {
            Pending = pending,
            Reasons = reasons,
            CheckedAt = checkedAt
        };
    }

    /// <summary>
    /// Bloco opcional <c>state</c> do envelope (sonda de estado do agente).
    /// Retorna <c>null</c> quando o agente não envia o bloco ou envia algo que
    /// não é objeto — compatibilidade total com agentes antigos.
    /// </summary>
    public static PatchServiceStateMeta? ParseState(object? state)
    {
        var element = ExtractObject(state);
        if (element is null)
            return null;

        var root = element.Value;

        string? serviceState = null;
        var rawServiceState = GetString(root, "serviceState") ?? GetString(root, "service");
        if (!string.IsNullOrWhiteSpace(rawServiceState) && ServiceStates.Contains(rawServiceState.Trim()))
            serviceState = rawServiceState.Trim().ToLowerInvariant();

        var meta = new PatchServiceStateMeta
        {
            ServiceState = serviceState,
            WuOnline = GetNullableBool(root, "wuOnline"),
            PolicyManaged = GetNullableBool(root, "policyManaged") ?? GetNullableBool(root, "isPolicyManaged"),
            // O agente envia os nomes tipados do WindowsUpdateState
            // (lastInstallSuccess/lastInstallTime/signature); os aliases curtos
            // existem para compatibilidade com envelopes antigos/experimentais.
            LastInstallSuccessAt = ParseDate(
                GetString(root, "lastInstallSuccess")
                ?? GetString(root, "lastInstallSuccessAt")
                ?? GetString(root, "lastSuccessTime")),
            DownloadInProgress = GetNullableBool(root, "downloadInProgress") ?? GetNullableBool(root, "isDownloading"),
            // wsusServer vazio significa "não configurado" e NUNCA sobrescreve
            // um servidor conhecido (o parser devolve null e o repositório ignora).
            WsusServer = string.IsNullOrWhiteSpace(GetString(root, "wsusServer"))
                ? null
                : Truncate(GetString(root, "wsusServer"), MaxWsusServer),
            AuOptions = GetNullableInt(root, "auOptions", 0, 5),
            NoAutoUpdate = GetNullableBool(root, "noAutoUpdate"),
            DeferralDays = GetNullableInt(root, "deferralDays", 0, 365),
            ActiveHoursStart = GetNullableInt(root, "activeHoursStart", 0, 23),
            ActiveHoursEnd = GetNullableInt(root, "activeHoursEnd", 0, 23),
            QfeInstalledCount = GetNullableInt(root, "qfeInstalledCount", 0, MaxCount),
            DownloadPercent = GetNullableInt(root, "downloadPercent", 0, 100),
            PolicyKnown = GetNullableBool(root, "policyKnown"),
            StateHash = Truncate(GetString(root, "signature") ?? GetString(root, "hash") ?? GetString(root, "stateHash"), 80),
            StateChangedAt = ParseDate(GetString(root, "changedAt") ?? GetString(root, "stateChangedAt"))
        };

        return meta.IsEmpty ? null : meta;
    }

    private static AgentWindowsUpdateEntry? MapEntry(JsonElement item)
    {
        var kbIds = PatchJson.ParseStringArray(GetRawString(item, "kbIds"))
            .Select(NormalizeKb)
            .Where(kb => kb is not null)
            .Select(kb => kb!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updateId = GetString(item, "updateId");
        var normalizedUpdateId = NormalizeGuid(updateId);
        var revision = 0;
        if (item.TryGetProperty("revision", out var revisionElement) &&
            revisionElement.ValueKind == JsonValueKind.Number &&
            revisionElement.TryGetInt32(out var parsedRevision) &&
            parsedRevision >= 0)
        {
            revision = parsedRevision;
        }

        var rawKey = GetString(item, "updateKey");
        var updateKey = NormalizeUpdateKey(rawKey, normalizedUpdateId, revision, kbIds);
        if (string.IsNullOrWhiteSpace(updateKey))
            return null;

        var isInstalled = GetBool(item, "isInstalled");
        var isPending = GetBool(item, "isPending") || !isInstalled;

        long? maxDownloadSize = null;
        if (item.TryGetProperty("maxDownloadSize", out var sizeElement) &&
            sizeElement.ValueKind == JsonValueKind.Number &&
            sizeElement.TryGetInt64(out var size) &&
            size >= 0)
        {
            maxDownloadSize = size;
        }

        return new AgentWindowsUpdateEntry
        {
            UpdateKey = updateKey,
            KbIds = kbIds.Count > 0 ? JsonSerializer.Serialize(kbIds) : null,
            UpdateId = normalizedUpdateId,
            Revision = revision,
            Title = Truncate(GetString(item, "title"), MaxTitle) ?? string.Empty,
            Description = Truncate(GetString(item, "description"), MaxDescription),
            Classification = Truncate(GetString(item, "classification"), MaxClassification),
            Categories = GetRawString(item, "categories"),
            Severity = Truncate(GetString(item, "severity"), MaxSeverity),
            IsPending = isPending,
            IsInstalled = isInstalled,
            IsHidden = GetBool(item, "isHidden"),
            IsDownloaded = GetBool(item, "isDownloaded"),
            IsMandatory = GetBool(item, "isMandatory"),
            RequiresReboot = GetBool(item, "requiresReboot"),
            MaxDownloadSize = maxDownloadSize,
            SupportUrl = Truncate(GetString(item, "supportUrl"), MaxSupportUrl),
            PublishedAt = ParseDate(GetString(item, "publishedAt")),
            LastDeployedAt = ParseDate(GetString(item, "lastDeployedAt")),
            InstalledAt = ParseDate(GetString(item, "installedAt")),
            Source = Truncate(GetString(item, "source"), MaxSource) ?? "wua",
            Raw = null
        };
    }

    private static string? NormalizeUpdateKey(
        string? rawKey,
        string? updateId,
        int revision,
        IReadOnlyList<string> kbIds)
    {
        var key = rawKey?.Trim() ?? string.Empty;
        if (key.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) && key.Count(ch => ch == ':') == 2)
            return key.ToLowerInvariant();
        if (key.StartsWith("kb:", StringComparison.OrdinalIgnoreCase))
        {
            var kb = NormalizeKb(key[3..]);
            return kb is null ? null : "kb:" + kb;
        }

        if (!string.IsNullOrWhiteSpace(updateId))
            return "guid:" + updateId + ":" + revision;

        return kbIds.Count > 0 ? "kb:" + kbIds[0] : null;
    }

    private static string? NormalizeGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return Guid.TryParse(raw.Trim(), out var guid) ? guid.ToString("D").ToLowerInvariant() : null;
    }

    private static string? NormalizeKb(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var value = raw.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        if (!value.StartsWith("KB", StringComparison.Ordinal))
            value = "KB" + value;
        var digits = value[2..];
        if (digits.Length is < 4 or > 10 || !digits.All(char.IsDigit))
            return null;
        return value;
    }

    private static JsonElement? ExtractArray(object? value)
    {
        var element = ToElement(value);
        return element is { ValueKind: JsonValueKind.Array } ? element : null;
    }

    private static JsonElement? ExtractObject(object? value)
    {
        var element = ToElement(value);
        return element is { ValueKind: JsonValueKind.Object } ? element : null;
    }

    private static JsonElement? ToElement(object? value)
    {
        if (value is null)
            return null;
        if (value is JsonElement element)
            return element;
        try
        {
            var json = JsonSerializer.Serialize(value);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetRawString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
            return null;
        return element.GetRawText();
    }

    private static string? GetString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        return element.GetString();
    }

    private static bool GetBool(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element))
            return false;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static int? GetNullableInt(JsonElement item, string property, int min, int max)
    {
        if (!item.TryGetProperty(property, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            return null;
        return value >= min && value <= max ? value : null;
    }

    private static bool? GetNullableBool(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) ? parsed : null,
            _ => null
        };
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}