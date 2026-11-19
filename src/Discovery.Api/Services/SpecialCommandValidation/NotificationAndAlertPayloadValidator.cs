using System.Text.Json;
using Discovery.Core.Helpers;

namespace Discovery.Api.Services;

internal static class NotificationAndAlertPayloadValidator
{
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

    internal static bool TryNormalizePsadtAlert(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "alertId", out var alertId, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "type", out var type, out validationError))
            return false;

        type = type.ToLowerInvariant();
        if (!PsadtTypes.Contains(type))
        {
            validationError = "field 'type' must be one of: modal, toast, update-progress.";
            return false;
        }

        if (type == "update-progress")
            return TryNormalizePsadtUpdateProgress(payload, alertId, out normalizedPayload, out validationError);

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "message", out var message, out validationError))
            return false;

        // waitForUser: modal que permanece aberto até o usuário clicar em OK.
        // Quando true, o timeout é ignorado (Show-ADTDialogBox sem -Timeout).
        var waitForUser = false;
        if (payload.TryGetProperty("waitForUser", out var waitForUserElement) && waitForUserElement.ValueKind != JsonValueKind.Null)
        {
            if (waitForUserElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                validationError = "field 'waitForUser' must be a boolean.";
                return false;
            }

            waitForUser = waitForUserElement.GetBoolean();
        }

        int? timeoutSeconds = null;
        if (!waitForUser)
        {
            timeoutSeconds = 120;
            if (payload.TryGetProperty("timeoutSeconds", out var timeoutElement) && timeoutElement.ValueKind != JsonValueKind.Null)
            {
                if (!PayloadJsonHelpers.TryReadPositiveInt(timeoutElement, out var parsedTimeout))
                {
                    validationError = "field 'timeoutSeconds' must be a positive integer.";
                    return false;
                }

                timeoutSeconds = parsedTimeout;
            }
        }

        var icon = "info";
        if (payload.TryGetProperty("icon", out var iconElement) && iconElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(iconElement, out var providedIcon))
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

                if (!PayloadJsonHelpers.TryGetRequiredString(actionElement, "label", out var label, out validationError))
                    return false;

                if (!PayloadJsonHelpers.TryGetRequiredString(actionElement, "value", out var value, out validationError))
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
            if (!PayloadJsonHelpers.TryReadString(defaultActionElement, out var parsedDefaultAction))
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
            ["waitForUser"] = waitForUser,
            ["icon"] = icon,
            ["actions"] = actions,
            ["defaultAction"] = defaultAction
        };

        // Modal com waitForUser não leva timeout: omite a chave para que o
        // Show-ADTDialogBox seja executado sem -Timeout (aguarda o clique).
        if (timeoutSeconds.HasValue)
        {
            normalized["timeoutSeconds"] = timeoutSeconds.Value;
        }

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
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

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "statusText", out var statusText, out validationError))
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
            if (!PayloadJsonHelpers.TryReadString(subtitleElement, out var parsedSubtitle))
            {
                validationError = "field 'subtitle' must be a string when provided.";
                return false;
            }

            subtitle = parsedSubtitle;
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var messageElement) && messageElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(messageElement, out var parsedMessage))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    internal static bool TryNormalizeNotification(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "notificationId", out var notificationId, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "idempotencyKey", out var idempotencyKey, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "title", out var title, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "message", out var message, out validationError, allowEmpty: true))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "eventType", out var eventType, out validationError))
            return false;

        var mode = "notify_only";
        if (payload.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(modeElement, out var providedMode))
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
            if (!PayloadJsonHelpers.TryReadString(severityElement, out var providedSeverity))
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
            if (!PayloadJsonHelpers.TryReadString(layoutElement, out var providedLayout))
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
            if (!PayloadJsonHelpers.TryReadPositiveInt(timeoutElement, out timeoutSeconds))
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

            metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataElement.GetRawText(), PayloadJsonHelpers.JsonOptions)
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Convite de chat de suporte. O payload só carrega metadados de exibição e
    /// correlação — o conteúdo das mensagens NUNCA trafega por comando.
    /// </summary>
    internal static bool TryNormalizeTicketChatInvite(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredGuid(payload, "sessionId", out var sessionId, out validationError))
            return false;

        // Opcional: conversas avulsas (sem chamado) não enviam ticketId.
        if (!PayloadJsonHelpers.TryGetOptionalGuid(payload, "ticketId", out var ticketId, out validationError))
            return false;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "ticketTitle", out var ticketTitle, out validationError, allowEmpty: true))
            return false;

        if (ticketTitle.Length > TicketChatInvitePayloadFactory.MaxTicketTitleLength)
        {
            validationError = $"field 'ticketTitle' must have at most {TicketChatInvitePayloadFactory.MaxTicketTitleLength} characters.";
            return false;
        }

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "supportUserName", out var supportUserName, out validationError, allowEmpty: true))
            return false;

        if (supportUserName.Length > TicketChatInvitePayloadFactory.MaxSupportUserNameLength)
        {
            validationError = $"field 'supportUserName' must have at most {TicketChatInvitePayloadFactory.MaxSupportUserNameLength} characters.";
            return false;
        }

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "expiresAtUtc", out var expiresAtUtcRaw, out validationError, allowEmpty: true))
            return false;

        if (!string.IsNullOrWhiteSpace(expiresAtUtcRaw) && !PayloadJsonHelpers.TryParseIsoUtc(expiresAtUtcRaw, out _))
        {
            validationError = "field 'expiresAtUtc' must be an ISO-8601 UTC timestamp.";
            return false;
        }

        normalizedPayload = TicketChatInvitePayloadFactory.Build(
            sessionId,
            ticketId,
            ticketTitle,
            supportUserName,
            string.IsNullOrWhiteSpace(expiresAtUtcRaw) ? null : expiresAtUtcRaw);

        return true;
    }
}
