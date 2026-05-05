using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

public sealed record NormalizedDashboardEvent(
    string EventType,
    JsonElement? Data,
    DateTime TimestampUtc,
    Guid? ClientId,
    Guid? SiteId);

public sealed class DashboardEventContractNormalizer
{
    private static readonly HashSet<string> CanonicalEventTypes = new(StringComparer.Ordinal)
    {
        "AgentHeartbeat",
        "AgentStatusChanged",
        "CommandCompleted",
        "AgentHardwareReported",
        "AgentConnected",
        "AgentDisconnected"
    };

    // REMOVE_AFTER_2026-06-01
    private static readonly Dictionary<string, string> LegacyEventTypeMap = new(StringComparer.Ordinal)
    {
        ["agent_connected"] = "AgentConnected",
        ["agent_disconnected"] = "AgentDisconnected",
        ["command_result"] = "CommandCompleted"
    };

    // REMOVE_AFTER_2026-06-01
    private static readonly Dictionary<string, string[]> LegacyFieldAliases = new(StringComparer.Ordinal)
    {
        ["agentId"] = ["id", "agentID"],
        ["timestampUtc"] = ["timestamp", "timeStamp"],
        ["cpuPercent"] = ["cpu"],
        ["memoryPercent"] = ["memory"],
        ["diskPercent"] = ["disk"],
        ["hostname"] = ["hostName", "machineName"],
        ["agentVersion"] = ["version", "agent_version"],
        ["memoryTotalGb"] = ["memoryTotal"],
        ["memoryUsedGb"] = ["memoryUsed"],
        ["diskTotalGb"] = ["diskTotal"],
        ["diskUsedGb"] = ["diskUsed"],
        ["p2pPeers"] = ["p2pPeersCount"],
        ["uptimeSeconds"] = ["uptime"],
        ["processCount"] = ["processes"],
        ["ipAddress"] = ["lastIpAddress", "ip"]
    };

    private readonly IOptions<RealtimeContractOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DashboardEventContractNormalizer> _logger;

    public DashboardEventContractNormalizer(
        IOptions<RealtimeContractOptions> options,
        TimeProvider timeProvider,
        ILogger<DashboardEventContractNormalizer> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool TryNormalize(string? payload, string source, out NormalizedDashboardEvent? normalizedEvent)
    {
        normalizedEvent = null;
        var strictMode = IsStrictMode();

        if (string.IsNullOrWhiteSpace(payload))
        {
            LogViolation(
                field: "envelope",
                expected: "valid JSON object",
                received: "empty",
                source: source,
                strictMode: strictMode);
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            LogViolation(
                field: "envelope",
                expected: "valid JSON object",
                received: "invalid_json",
                source: source,
                strictMode: strictMode);
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LogViolation(
                    field: "envelope",
                    expected: "JSON object",
                    received: root.ValueKind.ToString(),
                    source: source,
                    strictMode: strictMode);
                return false;
            }

            if (!TryGetRequiredString(root, "eventType", out var rawEventType))
            {
                LogViolation(
                    field: "eventType",
                    expected: "present",
                    received: "missing",
                    source: source,
                    strictMode: strictMode);
                return false;
            }

            if (!TryNormalizeEventType(rawEventType, source, strictMode, out var eventType))
                return false;

            if (!TryResolveTimestampUtc(root, source, strictMode, eventType, out var timestampUtc))
                return false;

            var hasData = root.TryGetProperty("data", out var rawData) && rawData.ValueKind != JsonValueKind.Null;
            Dictionary<string, JsonElement>? dataMap = null;
            JsonElement? normalizedData = null;

            if (hasData)
            {
                if (rawData.ValueKind == JsonValueKind.Object)
                {
                    dataMap = BuildDataMap(rawData);
                }
                else
                {
                    normalizedData = rawData.Clone();
                }
            }

            if (!TryResolveTenantId(root, dataMap, "clientId", source, strictMode, eventType, out var clientId))
                return false;

            if (!TryResolveTenantId(root, dataMap, "siteId", source, strictMode, eventType, out var siteId))
                return false;

            Guid? agentId = null;

            if (dataMap is not null)
            {
                if (!TryNormalizeDataObject(dataMap, source, strictMode, eventType, out var normalizedObjectData, out agentId))
                    return false;

                normalizedData = normalizedObjectData;
            }

            if (!ValidateEventIntegrity(eventType, normalizedData, source, strictMode, agentId))
                return false;

            normalizedEvent = new NormalizedDashboardEvent(
                EventType: eventType,
                Data: normalizedData,
                TimestampUtc: timestampUtc,
                ClientId: clientId,
                SiteId: siteId);

            return true;
        }
    }

    private bool TryNormalizeEventType(
        string rawEventType,
        string source,
        bool strictMode,
        out string normalizedEventType)
    {
        normalizedEventType = rawEventType;

        if (CanonicalEventTypes.Contains(rawEventType))
            return true;

        // REMOVE_AFTER_2026-06-01
        if (LegacyEventTypeMap.TryGetValue(rawEventType, out var mappedEventType))
        {
            if (strictMode)
            {
                LogViolation(
                    field: "eventType",
                    expected: "enum(6) PascalCase",
                    received: rawEventType,
                    source: source,
                    strictMode: strictMode,
                    eventType: rawEventType);
                return false;
            }

            LogViolation(
                field: "eventType",
                expected: "PascalCase",
                received: rawEventType,
                source: source,
                strictMode: strictMode,
                eventType: rawEventType);

            normalizedEventType = mappedEventType;
            return true;
        }

        LogViolation(
            field: "eventType",
            expected: "enum(6)",
            received: rawEventType,
            source: source,
            strictMode: strictMode,
            eventType: rawEventType);
        return false;
    }

    private bool TryResolveTimestampUtc(
        JsonElement root,
        string source,
        bool strictMode,
        string eventType,
        out DateTime timestampUtc)
    {
        timestampUtc = default;

        if (root.TryGetProperty("timestampUtc", out var timestampElement) && timestampElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(timestampElement, out var timestampRaw) || !TryParseUtc(timestampRaw, out timestampUtc))
            {
                LogViolation(
                    field: "timestampUtc",
                    expected: "ISO-8601",
                    received: timestampElement.GetRawText(),
                    source: source,
                    strictMode: strictMode,
                    eventType: eventType);
                return false;
            }

            return true;
        }

        JsonElement legacyTimestampElement = default;
        string? legacyFieldName = null;

        if (root.TryGetProperty("timestamp", out var timestampLegacy) && timestampLegacy.ValueKind != JsonValueKind.Null)
        {
            legacyTimestampElement = timestampLegacy;
            legacyFieldName = "timestamp";
        }
        else if (root.TryGetProperty("timeStamp", out var timestampLegacyAlt) && timestampLegacyAlt.ValueKind != JsonValueKind.Null)
        {
            legacyTimestampElement = timestampLegacyAlt;
            legacyFieldName = "timeStamp";
        }

        if (legacyFieldName is null)
        {
            LogViolation(
                field: "timestampUtc",
                expected: "present",
                received: "missing",
                source: source,
                strictMode: strictMode,
                eventType: eventType);
            return false;
        }

        if (strictMode)
        {
            LogViolation(
                field: "timestampUtc",
                expected: "timestampUtc",
                received: legacyFieldName,
                source: source,
                strictMode: strictMode,
                eventType: eventType);
            return false;
        }

        // REMOVE_AFTER_2026-06-01
        LogViolation(
            field: "timestampUtc",
            expected: "timestampUtc",
            received: legacyFieldName,
            source: source,
            strictMode: strictMode,
            eventType: eventType);

        if (!TryReadString(legacyTimestampElement, out var legacyTimestampRaw) || !TryParseUtc(legacyTimestampRaw, out timestampUtc))
        {
            LogViolation(
                field: "timestampUtc",
                expected: "ISO-8601",
                received: legacyTimestampElement.GetRawText(),
                source: source,
                strictMode: strictMode,
                eventType: eventType);
            return false;
        }

        return true;
    }

    private bool TryResolveTenantId(
        JsonElement root,
        Dictionary<string, JsonElement>? dataMap,
        string fieldName,
        string source,
        bool strictMode,
        string eventType,
        out Guid? resolvedId)
    {
        resolvedId = null;

        if (root.TryGetProperty(fieldName, out var rootTenantId) && rootTenantId.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadGuid(rootTenantId, out var parsedRootId))
            {
                LogViolation(
                    field: $"envelope.{fieldName}",
                    expected: "Guid",
                    received: rootTenantId.GetRawText(),
                    source: source,
                    strictMode: strictMode,
                    eventType: eventType,
                    agentId: TryExtractGuid(dataMap, "agentId"));
                return false;
            }

            resolvedId = parsedRootId;
            return true;
        }

        if (dataMap is not null && dataMap.TryGetValue(fieldName, out var dataTenantId))
        {
            if (!TryReadGuid(dataTenantId, out var parsedDataId))
            {
                LogViolation(
                    field: $"envelope.{fieldName}",
                    expected: "Guid",
                    received: dataTenantId.GetRawText(),
                    source: source,
                    strictMode: strictMode,
                    eventType: eventType,
                    agentId: TryExtractGuid(dataMap, "agentId"));
                return false;
            }

            if (strictMode)
            {
                LogViolation(
                    field: $"envelope.{fieldName}",
                    expected: "root_level",
                    received: "data_only",
                    source: source,
                    strictMode: strictMode,
                    eventType: eventType,
                    agentId: TryExtractGuid(dataMap, "agentId"));
                return false;
            }

            // REMOVE_AFTER_2026-06-01
            LogViolation(
                field: $"envelope.{fieldName}",
                expected: "root_level",
                received: "data_only",
                source: source,
                strictMode: strictMode,
                eventType: eventType,
                agentId: TryExtractGuid(dataMap, "agentId"));

            resolvedId = parsedDataId;
            dataMap.Remove(fieldName);
            return true;
        }

        return true;
    }

    private bool TryNormalizeDataObject(
        Dictionary<string, JsonElement> dataMap,
        string source,
        bool strictMode,
        string eventType,
        out JsonElement normalizedData,
        out Guid? agentId)
    {
        normalizedData = default;
        agentId = null;

        // REMOVE_AFTER_2026-06-01
        foreach (var (canonicalField, aliases) in LegacyFieldAliases)
        {
            var aliasMatches = aliases.Where(dataMap.ContainsKey).ToList();
            foreach (var alias in aliasMatches)
            {
                if (strictMode)
                {
                    LogViolation(
                        field: $"data.{canonicalField}",
                        expected: canonicalField,
                        received: alias,
                        source: source,
                        strictMode: strictMode,
                        eventType: eventType,
                        agentId: TryExtractGuid(dataMap, "agentId"));
                    return false;
                }

                if (!dataMap.ContainsKey(canonicalField))
                    dataMap[canonicalField] = dataMap[alias];

                dataMap.Remove(alias);

                LogViolation(
                    field: $"data.{canonicalField}",
                    expected: canonicalField,
                    received: alias,
                    source: source,
                    strictMode: strictMode,
                    eventType: eventType,
                    agentId: TryExtractGuid(dataMap, "agentId"));
            }
        }

        agentId = TryExtractGuid(dataMap, "agentId");

        var normalizedJson = JsonSerializer.Serialize(dataMap);
        using var normalizedDocument = JsonDocument.Parse(normalizedJson);
        normalizedData = normalizedDocument.RootElement.Clone();
        return true;
    }

    private bool ValidateEventIntegrity(
        string eventType,
        JsonElement? normalizedData,
        string source,
        bool strictMode,
        Guid? agentId)
    {
        Dictionary<string, JsonElement>? dataMap = null;
        if (normalizedData.HasValue && normalizedData.Value.ValueKind == JsonValueKind.Object)
            dataMap = BuildDataMap(normalizedData.Value);

        switch (eventType)
        {
            case "AgentHeartbeat":
                return ValidateGuidField(dataMap, "agentId", source, strictMode, eventType, agentId);

            case "AgentStatusChanged":
                if (!ValidateGuidField(dataMap, "agentId", source, strictMode, eventType, agentId))
                    return false;

                if (!TryGetNonEmptyString(dataMap, "status", out _))
                {
                    LogViolation(
                        field: "data.status",
                        expected: "present",
                        received: "missing",
                        source: source,
                        strictMode: strictMode,
                        eventType: eventType,
                        agentId: agentId);
                    return false;
                }

                return true;

            case "CommandCompleted":
                return ValidateGuidField(dataMap, "commandId", source, strictMode, eventType, agentId);

            case "AgentHardwareReported":
                return ValidateGuidField(dataMap, "agentId", source, strictMode, eventType, agentId);

            case "AgentConnected":
            case "AgentDisconnected":
                if (!ValidateGuidField(dataMap, "agentId", source, strictMode, eventType, agentId))
                    return false;

                if (!TryGetNonEmptyString(dataMap, "transport", out _))
                {
                    // Accepted with warning even in hardening.
                    LogViolation(
                        field: "data.transport",
                        expected: "present",
                        received: "missing",
                        source: source,
                        strictMode: strictMode,
                        eventType: eventType,
                        agentId: agentId,
                        forceWarning: true);
                }

                return true;

            default:
                return true;
        }
    }

    private bool ValidateGuidField(
        Dictionary<string, JsonElement>? dataMap,
        string fieldName,
        string source,
        bool strictMode,
        string eventType,
        Guid? agentId)
    {
        if (dataMap is null || !dataMap.TryGetValue(fieldName, out var value))
        {
            LogViolation(
                field: $"data.{fieldName}",
                expected: "Guid",
                received: "missing",
                source: source,
                strictMode: strictMode,
                eventType: eventType,
                agentId: agentId);
            return false;
        }

        if (!TryReadGuid(value, out _))
        {
            LogViolation(
                field: $"data.{fieldName}",
                expected: "Guid",
                received: value.GetRawText(),
                source: source,
                strictMode: strictMode,
                eventType: eventType,
                agentId: agentId);
            return false;
        }

        return true;
    }

    private static Dictionary<string, JsonElement> BuildDataMap(JsonElement element)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
            map[prop.Name] = prop.Value.Clone();

        return map;
    }

    private static bool TryReadString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadGuid(JsonElement element, out Guid value)
    {
        value = Guid.Empty;
        return TryReadString(element, out var raw) && Guid.TryParse(raw, out value);
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return false;

        if (!TryReadString(prop, out value))
            return false;

        value = value.Trim();
        return value.Length > 0;
    }

    private static bool TryParseUtc(string rawValue, out DateTime parsedUtc)
    {
        parsedUtc = default;
        if (!DateTimeOffset.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return false;
        }

        parsedUtc = dto.UtcDateTime;
        return true;
    }

    private static bool TryGetNonEmptyString(Dictionary<string, JsonElement>? dataMap, string fieldName, out string value)
    {
        value = string.Empty;
        if (dataMap is null || !dataMap.TryGetValue(fieldName, out var field))
            return false;

        if (!TryReadString(field, out value))
            return false;

        value = value.Trim();
        return value.Length > 0;
    }

    private static Guid? TryExtractGuid(Dictionary<string, JsonElement>? dataMap, string fieldName)
    {
        if (dataMap is null || !dataMap.TryGetValue(fieldName, out var field))
            return null;

        return TryReadGuid(field, out var id) ? id : null;
    }

    private bool IsStrictMode()
    {
        var mode = (_options.Value.Mode ?? "auto").Trim();

        if (mode.Equals("strict", StringComparison.OrdinalIgnoreCase))
            return true;

        if (mode.Equals("transition", StringComparison.OrdinalIgnoreCase))
            return false;

        var hardeningDateUtc = _options.Value.HardeningDateUtc;
        if (hardeningDateUtc.Kind == DateTimeKind.Unspecified)
            hardeningDateUtc = DateTime.SpecifyKind(hardeningDateUtc, DateTimeKind.Utc);
        else
            hardeningDateUtc = hardeningDateUtc.ToUniversalTime();

        return _timeProvider.GetUtcNow().UtcDateTime >= hardeningDateUtc;
    }

    private void LogViolation(
        string field,
        string expected,
        string received,
        string source,
        bool strictMode,
        string? eventType = null,
        Guid? agentId = null,
        bool forceWarning = false)
    {
        const string message = "[CONTRACT_VIOLATION] component=Server field={Field} expected={Expected} received={Received} source={Source} eventType={EventType} agentId={AgentId}";

        var resolvedEventType = string.IsNullOrWhiteSpace(eventType) ? "-" : eventType;
        var resolvedAgentId = agentId?.ToString() ?? "-";

        if (forceWarning || !strictMode)
        {
            _logger.LogWarning(message, field, expected, received, source, resolvedEventType, resolvedAgentId);
            return;
        }

        _logger.LogError(message, field, expected, received, source, resolvedEventType, resolvedAgentId);
    }
}