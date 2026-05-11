using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Api.Services;

public sealed class SpecialCommandPayloadValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> RemoteDebugActions = new(StringComparer.Ordinal)
    {
        "start",
        "stop"
    };

    private static readonly HashSet<string> RemoteDebugLogLevels = new(StringComparer.Ordinal)
    {
        "trace",
        "info",
        "debug",
        "warn",
        "error"
    };

    private static readonly HashSet<string> PsadtTypes = new(StringComparer.Ordinal)
    {
        "modal",
        "toast"
    };

    private static readonly HashSet<string> PsadtIcons = new(StringComparer.Ordinal)
    {
        "info",
        "warning",
        "error",
        "question"
    };

    private static readonly HashSet<string> NotificationModes = new(StringComparer.Ordinal)
    {
        "notify_only",
        "interactive"
    };

    private static readonly HashSet<string> NotificationSeverities = new(StringComparer.Ordinal)
    {
        "low",
        "medium",
        "high",
        "critical"
    };

    private static readonly HashSet<string> NotificationLayouts = new(StringComparer.Ordinal)
    {
        "toast",
        "modal",
        "banner"
    };

    private static readonly HashSet<string> UpdateActions = new(StringComparer.Ordinal)
    {
        "check-update",
        "install",
        "rollback"
    };

    private static readonly Regex SemVerRegex = new(
        "^\\d+\\.\\d+\\.\\d+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool TryNormalize(
        CommandType commandType,
        string payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = payload;
        validationError = string.Empty;

        if (!CommandTypeWireMapper.IsSpecialCommand(commandType))
            return true;

        if (string.IsNullOrWhiteSpace(payload))
        {
            validationError = "payload must be a non-empty JSON object.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            validationError = $"payload must be valid JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "payload must be a JSON object.";
                return false;
            }

            return commandType switch
            {
                CommandType.RemoteDebug => TryNormalizeRemoteDebug(document.RootElement, out normalizedPayload, out validationError),
                CommandType.ShowPsadtAlert => TryNormalizePsadtAlert(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Notification => TryNormalizeNotification(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Update => TryNormalizeUpdate(document.RootElement, out normalizedPayload, out validationError),
                _ => true
            };
        }
    }

    private static bool TryNormalizeRemoteDebug(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!RemoteDebugActions.Contains(action))
        {
            validationError = "field 'action' must be one of: start, stop.";
            return false;
        }

        if (!TryGetRequiredGuid(payload, "sessionId", out var sessionId, out validationError))
            return false;

        var logLevel = "info";
        if (payload.TryGetProperty("logLevel", out var logLevelElement) && logLevelElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(logLevelElement, out var providedLogLevel))
            {
                validationError = "field 'logLevel' must be a string.";
                return false;
            }

            logLevel = providedLogLevel.ToLowerInvariant();
            if (!RemoteDebugLogLevels.Contains(logLevel))
            {
                validationError = "field 'logLevel' must be one of: info, debug, warn, error.";
                return false;
            }
        }

        if (!payload.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object)
        {
            validationError = "field 'stream' must be a JSON object.";
            return false;
        }

        if (!TryGetRequiredString(stream, "natsSubject", out var natsSubject, out validationError))
            return false;

        if (!natsSubject.StartsWith("tenant.", StringComparison.Ordinal) ||
            !natsSubject.EndsWith(".remote-debug.log", StringComparison.Ordinal))
        {
            validationError = "field 'stream.natsSubject' must match tenant-scoped remote-debug.log subject.";
            return false;
        }

        string? expiresAtUtc = null;
        if (action == "start")
        {
            if (!TryGetRequiredString(payload, "expiresAtUtc", out var expiresRaw, out validationError))
                return false;

            if (!TryParseIsoUtc(expiresRaw, out var expiresAt))
            {
                validationError = "field 'expiresAtUtc' must be an ISO-8601 UTC datetime string.";
                return false;
            }

            expiresAtUtc = expiresAt.ToString("O");
        }
        else if (payload.TryGetProperty("expiresAtUtc", out var optionalExpires) && optionalExpires.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(optionalExpires, out var optionalRaw) || !TryParseIsoUtc(optionalRaw, out var optionalExpiresAt))
            {
                validationError = "field 'expiresAtUtc' must be an ISO-8601 UTC datetime string when provided.";
                return false;
            }

            expiresAtUtc = optionalExpiresAt.ToString("O");
        }

        var streamPayload = new Dictionary<string, object?>
        {
            ["natsSubject"] = natsSubject
        };

        var normalized = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["sessionId"] = sessionId,
            ["logLevel"] = logLevel,
            ["expiresAtUtc"] = expiresAtUtc,
            ["stream"] = streamPayload
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizePsadtAlert(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "alertId", out var alertId, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "type", out var type, out validationError))
            return false;

        type = type.ToLowerInvariant();
        if (!PsadtTypes.Contains(type))
        {
            validationError = "field 'type' must be one of: modal, toast.";
            return false;
        }

        if (!TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "message", out var message, out validationError))
            return false;

        var timeoutSeconds = 120;
        if (payload.TryGetProperty("timeoutSeconds", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadPositiveInt(timeoutElement, out timeoutSeconds))
            {
                validationError = "field 'timeoutSeconds' must be a positive integer.";
                return false;
            }
        }

        var icon = "info";
        if (payload.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(iconElement, out var providedIcon))
            {
                validationError = "field 'icon' must be a string.";
                return false;
            }

            icon = providedIcon.ToLowerInvariant();
            if (!PsadtIcons.Contains(icon))
            {
                validationError = "field 'icon' must be one of: info, warning, error, question.";
                return false;
            }
        }

        List<Dictionary<string, string>>? actions = null;
        if (payload.TryGetProperty("actions", out var actionsElement) && actionsElement.ValueKind != JsonValueKind.Null)
        {
            if (actionsElement.ValueKind != JsonValueKind.Array)
            {
                validationError = "field 'actions' must be an array when provided.";
                return false;
            }

            actions = [];
            foreach (var actionElement in actionsElement.EnumerateArray())
            {
                if (actionElement.ValueKind != JsonValueKind.Object)
                {
                    validationError = "each item in 'actions' must be an object with label/value.";
                    return false;
                }

                if (!TryGetRequiredString(actionElement, "label", out var label, out validationError))
                    return false;

                if (!TryGetRequiredString(actionElement, "value", out var value, out validationError))
                    return false;

                actions.Add(new Dictionary<string, string>
                {
                    ["label"] = label,
                    ["value"] = value
                });
            }
        }

        string? defaultAction = null;
        if (payload.TryGetProperty("defaultAction", out var defaultActionElement) && defaultActionElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(defaultActionElement, out var parsedDefaultAction))
            {
                validationError = "field 'defaultAction' must be a string when provided.";
                return false;
            }

            defaultAction = parsedDefaultAction;
        }

        var normalized = new Dictionary<string, object?>
        {
            ["alertId"] = alertId,
            ["type"] = type,
            ["title"] = title,
            ["message"] = message,
            ["timeoutSeconds"] = timeoutSeconds,
            ["icon"] = icon,
            ["actions"] = actions,
            ["defaultAction"] = defaultAction
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeNotification(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "notificationId", out var notificationId, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "idempotencyKey", out var idempotencyKey, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "message", out var message, out validationError, allowEmpty: true))
            return false;

        if (!TryGetRequiredString(payload, "eventType", out var eventType, out validationError))
            return false;

        var mode = "notify_only";
        if (payload.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(modeElement, out var providedMode))
            {
                validationError = "field 'mode' must be a string.";
                return false;
            }

            mode = providedMode.ToLowerInvariant();
            if (!NotificationModes.Contains(mode))
            {
                validationError = "field 'mode' must be one of: notify_only, interactive.";
                return false;
            }
        }

        if (mode != "interactive" && string.IsNullOrWhiteSpace(message))
        {
            validationError = "field 'message' must be non-empty when mode is not 'interactive'.";
            return false;
        }

        var severity = "medium";
        if (payload.TryGetProperty("severity", out var severityElement) && severityElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(severityElement, out var providedSeverity))
            {
                validationError = "field 'severity' must be a string.";
                return false;
            }

            severity = providedSeverity.ToLowerInvariant();
            if (!NotificationSeverities.Contains(severity))
            {
                validationError = "field 'severity' must be one of: low, medium, high, critical.";
                return false;
            }
        }

        var layout = "toast";
        if (payload.TryGetProperty("layout", out var layoutElement) && layoutElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(layoutElement, out var providedLayout))
            {
                validationError = "field 'layout' must be a string.";
                return false;
            }

            layout = providedLayout.ToLowerInvariant();
            if (!NotificationLayouts.Contains(layout))
            {
                validationError = "field 'layout' must be one of: toast, modal, banner.";
                return false;
            }
        }

        var timeoutSeconds = 8;
        if (payload.TryGetProperty("timeoutSeconds", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadPositiveInt(timeoutElement, out timeoutSeconds))
            {
                validationError = "field 'timeoutSeconds' must be a positive integer.";
                return false;
            }
        }

        object metadata = new Dictionary<string, object?>();
        if (payload.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind != JsonValueKind.Null)
        {
            if (metadataElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'metadata' must be a JSON object when provided.";
                return false;
            }

            metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataElement.GetRawText(), JsonOptions)
                ?? new Dictionary<string, object?>();
        }

        var normalized = new Dictionary<string, object?>
        {
            ["notificationId"] = notificationId,
            ["idempotencyKey"] = idempotencyKey,
            ["title"] = title,
            ["message"] = message,
            ["mode"] = mode,
            ["severity"] = severity,
            ["eventType"] = eventType,
            ["layout"] = layout,
            ["timeoutSeconds"] = timeoutSeconds,
            ["metadata"] = metadata
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeUpdate(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!UpdateActions.Contains(action))
        {
            validationError = "field 'action' must be one of: check-update, install, rollback.";
            return false;
        }

        string? version = null;
        if (payload.TryGetProperty("version", out var versionElement) && versionElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(versionElement, out var providedVersion))
            {
                validationError = "field 'version' must be a string when provided.";
                return false;
            }

            version = providedVersion.Trim();
            if (version.Length == 0)
                version = null;
        }

        if ((action == "install" || action == "rollback") && string.IsNullOrWhiteSpace(version))
        {
            validationError = "field 'version' is required for action 'install' and 'rollback'.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(version) && !SemVerRegex.IsMatch(version))
        {
            validationError = "field 'version' must be a valid semantic version.";
            return false;
        }

        string? url = null;
        if (payload.TryGetProperty("url", out var urlElement) && urlElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(urlElement, out var providedUrl))
            {
                validationError = "field 'url' must be a string URL when provided.";
                return false;
            }

            url = providedUrl.Trim();
            if (url.Length == 0)
                url = null;
        }

        if (action == "install")
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                validationError = "field 'url' is required for action 'install'.";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedInstallUrl) ||
                !string.Equals(parsedInstallUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                validationError = "field 'url' must be an absolute HTTPS URL for action 'install'.";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
                !string.Equals(parsedUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                validationError = "field 'url' must be an absolute HTTPS URL when provided.";
                return false;
            }
        }

        var normalized = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["version"] = version,
            ["url"] = url
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement payload,
        string propertyName,
        out string value,
        out string validationError,
        bool allowEmpty = false)
    {
        value = string.Empty;
        validationError = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            validationError = $"field '{propertyName}' is required.";
            return false;
        }

        if (!TryReadString(element, out value))
        {
            validationError = $"field '{propertyName}' must be a string.";
            return false;
        }

        value = value.Trim();
        if (!allowEmpty && value.Length == 0)
        {
            validationError = $"field '{propertyName}' must be non-empty.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredGuid(
        JsonElement payload,
        string propertyName,
        out Guid value,
        out string validationError)
    {
        value = Guid.Empty;
        validationError = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            validationError = $"field '{propertyName}' is required.";
            return false;
        }

        if (!TryReadString(element, out var raw) || !Guid.TryParse(raw, out value))
        {
            validationError = $"field '{propertyName}' must be a valid GUID string.";
            return false;
        }

        return true;
    }

    private static bool TryReadPositiveInt(JsonElement element, out int value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetInt32(out value) || value <= 0)
                return false;

            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value > 0)
        {
            return true;
        }

        return false;
    }

    private static bool TryReadString(JsonElement element, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryParseIsoUtc(string raw, out DateTime parsedUtc)
    {
        parsedUtc = default;
        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto))
        {
            return false;
        }

        parsedUtc = dto.UtcDateTime;
        return true;
    }
}