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
        "toast",
        "update-progress"
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

    private static readonly HashSet<string> StartupItemTypes = new(StringComparer.Ordinal)
    {
        "registry",
        "folder",
        "service"
    };

    private static readonly HashSet<string> EnableDisableActions = new(StringComparer.Ordinal)
    {
        "enable",
        "disable"
    };

    private static readonly HashSet<string> ScheduledTaskActions = new(StringComparer.Ordinal)
    {
        "enable",
        "disable",
        "run",
        "delete",
        "edit"
    };

    private static readonly HashSet<string> ScheduledTaskTriggerTypes = new(StringComparer.Ordinal)
    {
        "daily",
        "weekly",
        "once",
        "logon",
        "boot"
    };

    private static readonly Regex SemVerRegex = new(
        "^\\d+\\.\\d+\\.\\d+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MacAddressRegex = new(
        "^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Hive de um item de inicialização: HKLM, HKCU ou HKU:&lt;SID&gt; (itens de
    /// outros usuários). Restringir o formato evita que um agente comprometido
    /// monte caminhos de registro arbitrários via "hive".
    /// </summary>
    private static readonly Regex StartupItemHiveRegex = new(
        "^(HKLM|HKCU|HKU:S-1-[0-9-]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
                CommandType.Restart => TryNormalizeRestart(document.RootElement, out normalizedPayload, out validationError),
                CommandType.Shutdown => TryNormalizeShutdown(document.RootElement, out normalizedPayload, out validationError),
                CommandType.WakeOnLan => TryNormalizeWakeOnLan(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionStart => TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionStop => TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RemoteSessionQuality => TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RecordingStart => TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.RecordingStop => TryNormalizeRemoteSession(document.RootElement, out normalizedPayload, out validationError),
                CommandType.P2pPreload => TryNormalizeP2pPreload(document.RootElement, out normalizedPayload, out validationError),
                CommandType.StartupItem => TryNormalizeStartupItem(document.RootElement, out normalizedPayload, out validationError),
                CommandType.ScheduledTask => TryNormalizeScheduledTask(document.RootElement, out normalizedPayload, out validationError),
                CommandType.SoftwareUpdate => TryNormalizeSoftwareUpdate(document.RootElement, out normalizedPayload, out validationError),
                CommandType.SoftwareUninstall => TryNormalizeSoftwareUninstall(document.RootElement, out normalizedPayload, out validationError),
                _ => true
            };
        }
    }

    private static bool TryNormalizeRemoteSession(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        // Remote session payloads são JSON simples com action + sessionId + parâmetros.
        // A validação de schema é feita pelo handler; aqui apenas normalizamos (re-serializamos).
        normalizedPayload = payload.GetRawText();
        validationError = string.Empty;
        return true;
    }

    private static bool TryNormalizeP2pPreload(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (action is not ("preload" or "cancel"))
        {
            validationError = "field 'action' must be one of: preload, cancel.";
            return false;
        }

        if (!payload.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() == 0)
        {
            validationError = "field 'packages' must be a non-empty array.";
            return false;
        }

        foreach (var pkg in packages.EnumerateArray())
        {
            if (pkg.ValueKind != JsonValueKind.Object
                || !pkg.TryGetProperty("packageId", out var packageId)
                || packageId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(packageId.GetString()))
            {
                validationError = "each item in 'packages' must be an object with a non-empty string 'packageId'.";
                return false;
            }
        }

        normalizedPayload = payload.GetRawText();
        return true;
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
            validationError = "field 'type' must be one of: modal, toast, update-progress.";
            return false;
        }

        if (type == "update-progress")
            return TryNormalizePsadtUpdateProgress(payload, alertId, out normalizedPayload, out validationError);

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

    private static bool TryNormalizePsadtUpdateProgress(
        JsonElement payload,
        string alertId,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!TryGetRequiredString(payload, "statusText", out var statusText, out validationError))
            return false;

        var progressPercent = 0;
        if (payload.TryGetProperty("progressPercent", out var progressElement) && progressElement.ValueKind != JsonValueKind.Null)
        {
            if (!progressElement.TryGetInt32(out progressPercent) || progressPercent < 0 || progressPercent > 100)
            {
                validationError = "field 'progressPercent' must be an integer between 0 and 100.";
                return false;
            }
        }

        string? subtitle = null;
        if (payload.TryGetProperty("subtitle", out var subtitleElement) && subtitleElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(subtitleElement, out var parsedSubtitle))
            {
                validationError = "field 'subtitle' must be a string when provided.";
                return false;
            }

            subtitle = parsedSubtitle;
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var messageElement) && messageElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(messageElement, out var parsedMessage))
            {
                validationError = "field 'message' must be a string when provided.";
                return false;
            }

            message = parsedMessage;
        }

        var normalized = new Dictionary<string, object?>
        {
            ["alertId"] = alertId,
            ["type"] = "update-progress",
            ["title"] = title,
            ["statusText"] = statusText,
            ["progressPercent"] = progressPercent,
            ["subtitle"] = subtitle,
            ["message"] = message
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

    private static bool TryNormalizeRestart(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        var delaySeconds = 15;
        if (payload.TryGetProperty("delaySeconds", out var delayElement) && delayElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadPositiveInt(delayElement, out delaySeconds))
            {
                validationError = "field 'delaySeconds' must be a positive integer.";
                return false;
            }
        }

        var force = false;
        if (payload.TryGetProperty("force", out var forceElement) && forceElement.ValueKind != JsonValueKind.Null)
        {
            if (forceElement.ValueKind == JsonValueKind.True || forceElement.ValueKind == JsonValueKind.False)
            {
                force = forceElement.GetBoolean();
            }
            else
            {
                validationError = "field 'force' must be a boolean.";
                return false;
            }
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var msgElement) && msgElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(msgElement, out var providedMessage))
            {
                validationError = "field 'message' must be a string when provided.";
                return false;
            }

            message = providedMessage.Trim();
            if (message.Length > 512)
            {
                validationError = "field 'message' must be at most 512 characters.";
                return false;
            }
        }

        var normalized = new Dictionary<string, object?>
        {
            ["delaySeconds"] = delaySeconds,
            ["force"] = force,
            ["message"] = message
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeShutdown(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        var delaySeconds = 30;
        if (payload.TryGetProperty("delaySeconds", out var delayElement) && delayElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadPositiveInt(delayElement, out delaySeconds))
            {
                validationError = "field 'delaySeconds' must be a positive integer.";
                return false;
            }
        }

        var force = false;
        if (payload.TryGetProperty("force", out var forceElement) && forceElement.ValueKind != JsonValueKind.Null)
        {
            if (forceElement.ValueKind == JsonValueKind.True || forceElement.ValueKind == JsonValueKind.False)
            {
                force = forceElement.GetBoolean();
            }
            else
            {
                validationError = "field 'force' must be a boolean.";
                return false;
            }
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var msgElement) && msgElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(msgElement, out var providedMessage))
            {
                validationError = "field 'message' must be a string when provided.";
                return false;
            }

            message = providedMessage.Trim();
            if (message.Length > 512)
            {
                validationError = "field 'message' must be at most 512 characters.";
                return false;
            }
        }

        var normalized = new Dictionary<string, object?>
        {
            ["delaySeconds"] = delaySeconds,
            ["force"] = force,
            ["message"] = message
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeWakeOnLan(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "macAddress", out var macAddress, out validationError))
            return false;

        if (!MacAddressRegex.IsMatch(macAddress))
        {
            validationError = "field 'macAddress' must be a valid MAC address (e.g., 00:11:22:33:44:55).";
            return false;
        }

        string? broadcastAddress = null;
        if (payload.TryGetProperty("broadcastAddress", out var broadcastElement) && broadcastElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(broadcastElement, out var providedBroadcast))
            {
                validationError = "field 'broadcastAddress' must be a string when provided.";
                return false;
            }

            broadcastAddress = providedBroadcast.Trim();
            if (broadcastAddress.Length == 0)
                broadcastAddress = null;
        }

        var normalized = new Dictionary<string, object?>
        {
            ["macAddress"] = macAddress,
            ["broadcastAddress"] = broadcastAddress
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de atualização de software: exige packageId e
    /// canonicaliza installationType/source para winget|chocolatey.
    /// </summary>
    private static bool TryNormalizeSoftwareUpdate(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "packageId", out var packageId, out validationError))
            return false;

        packageId = packageId.Trim();
        if (packageId.Length == 0)
        {
            validationError = "field 'packageId' must be a non-empty string.";
            return false;
        }

        var installationType = "winget";
        if (payload.TryGetProperty("installationType", out var typeElement) && typeElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(typeElement, out var providedType))
            {
                validationError = "field 'installationType' must be a string.";
                return false;
            }

            var normalizedType = providedType.Trim().ToLowerInvariant();
            installationType = normalizedType.Contains("choco") ? "chocolatey" : "winget";
        }
        else if (payload.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(sourceElement, out var providedSource))
            {
                validationError = "field 'source' must be a string.";
                return false;
            }

            installationType = providedSource.Trim().ToLowerInvariant().Contains("choco") ? "chocolatey" : "winget";
        }

        var normalized = new Dictionary<string, object?>
        {
            ["packageId"] = packageId,
            ["installationType"] = installationType,
            ["source"] = installationType
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de desinstalação: exige name e canonicaliza
    /// installationType/source; packageId e os identificadores do registro são
    /// opcionais (o agent escolhe a melhor estratégia).
    /// </summary>
    private static bool TryNormalizeSoftwareUninstall(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "name", out var name, out validationError))
            return false;

        name = name.Trim();
        if (name.Length == 0)
        {
            validationError = "field 'name' must be a non-empty string.";
            return false;
        }

        string? OptionalString(string property)
        {
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
                return null;
            if (!TryReadString(element, out var value))
                return null;
            return value.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }

        var typeHint = OptionalString("installationType") ?? OptionalString("source") ?? "winget";
        var installationType = typeHint.ToLowerInvariant().Contains("choco") ? "chocolatey" : "winget";

        var normalized = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["packageId"] = OptionalString("packageId"),
            ["installationType"] = installationType,
            ["source"] = installationType,
            ["installId"] = OptionalString("installId"),
            ["serial"] = OptionalString("serial"),
            ["installSource"] = OptionalString("installSource")
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeStartupItem(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!EnableDisableActions.Contains(action))
        {
            validationError = "field 'action' must be one of: enable, disable.";
            return false;
        }

        if (!TryGetRequiredString(payload, "name", out var name, out validationError))
            return false;

        var itemType = "registry";
        if (payload.TryGetProperty("type", out var typeElement) && typeElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(typeElement, out var providedType))
            {
                validationError = "field 'type' must be a string.";
                return false;
            }

            itemType = providedType.Trim().ToLowerInvariant();
            if (!StartupItemTypes.Contains(itemType))
            {
                validationError = "field 'type' must be one of: registry, folder, service.";
                return false;
            }
        }

        string? source = null;
        if (payload.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(sourceElement, out var providedSource))
            {
                validationError = "field 'source' must be a string.";
                return false;
            }

            source = providedSource.Trim();
            if (source.Length == 0)
                source = null;
        }

        string? hive = null;
        if (payload.TryGetProperty("hive", out var hiveElement) && hiveElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(hiveElement, out var providedHive))
            {
                validationError = "field 'hive' must be a string.";
                return false;
            }

            hive = providedHive.Trim();
            if (hive.Length == 0)
            {
                hive = null;
            }
            else if (!StartupItemHiveRegex.IsMatch(hive))
            {
                validationError = "field 'hive' must be HKLM, HKCU or HKU:<SID>.";
                return false;
            }
        }

        var normalized = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["type"] = itemType,
            ["name"] = name,
            ["source"] = source,
            ["hive"] = hive
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, JsonOptions);
        return true;
    }

    private static bool TryNormalizeScheduledTask(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!ScheduledTaskActions.Contains(action))
        {
            validationError = "field 'action' must be one of: enable, disable, run, delete, edit.";
            return false;
        }

        if (!TryGetRequiredString(payload, "taskName", out var taskName, out validationError))
            return false;

        string? taskPath = null;
        if (payload.TryGetProperty("taskPath", out var taskPathElement) && taskPathElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadString(taskPathElement, out var providedPath))
            {
                validationError = "field 'taskPath' must be a string.";
                return false;
            }

            taskPath = providedPath.Trim();
            if (taskPath.Length == 0)
                taskPath = null;
        }

        Dictionary<string, object?>? edit = null;
        if (action == "edit")
        {
            if (!payload.TryGetProperty("edit", out var editElement) || editElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'edit' must be an object when action is 'edit'.";
                return false;
            }

            if (!TryGetRequiredString(editElement, "triggerType", out var triggerType, out validationError))
                return false;

            triggerType = triggerType.ToLowerInvariant();
            if (!ScheduledTaskTriggerTypes.Contains(triggerType))
            {
                validationError = "field 'edit.triggerType' must be one of: daily, weekly, once, logon, boot.";
                return false;
            }

            edit = new Dictionary<string, object?> { ["triggerType"] = triggerType };

            if (triggerType is "daily" or "weekly" or "once")
            {
                if (!TryGetRequiredString(editElement, "time", out var time, out validationError))
                    return false;

                edit["time"] = time;
            }

            if (triggerType == "weekly")
            {
                var days = new List<int>();
                if (editElement.TryGetProperty("daysOfWeek", out var daysElement) && daysElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var day in daysElement.EnumerateArray())
                    {
                        if (!day.TryGetInt32(out var dayValue) || dayValue < 0 || dayValue > 6)
                        {
                            validationError = "field 'edit.daysOfWeek' must contain integers between 0 (Sunday) and 6 (Saturday).";
                            return false;
                        }

                        days.Add(dayValue);
                    }
                }

                if (days.Count == 0)
                {
                    validationError = "field 'edit.daysOfWeek' must contain at least one day for weekly triggers.";
                    return false;
                }

                edit["daysOfWeek"] = days;
            }

            if (triggerType == "daily" && editElement.TryGetProperty("daysInterval", out var intervalElement))
            {
                if (intervalElement.TryGetInt32(out var intervalValue) && intervalValue > 1)
                    edit["daysInterval"] = intervalValue;
            }

            if (editElement.TryGetProperty("actionPath", out var actionPathElement) && actionPathElement.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadString(actionPathElement, out var providedActionPath))
                {
                    validationError = "field 'edit.actionPath' must be a string.";
                    return false;
                }

                var actionPathValue = providedActionPath.Trim();
                if (actionPathValue.Length > 0)
                    edit["actionPath"] = actionPathValue;

                if (editElement.TryGetProperty("actionArgs", out var actionArgsElement) && actionArgsElement.ValueKind != JsonValueKind.Null)
                {
                    if (!TryReadString(actionArgsElement, out var providedActionArgs))
                    {
                        validationError = "field 'edit.actionArgs' must be a string.";
                        return false;
                    }

                    var actionArgsValue = providedActionArgs.Trim();
                    if (actionArgsValue.Length > 0)
                        edit["actionArgs"] = actionArgsValue;
                }
            }
        }

        var normalized = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["taskPath"] = taskPath,
            ["taskName"] = taskName,
            ["edit"] = edit
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