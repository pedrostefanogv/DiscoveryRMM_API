using System.Text.Json;

namespace Discovery.Api.Services;

internal static class RemoteSessionPayloadValidator
{
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

    /// <summary>Modos aceitos da política de teto de banda.</summary>
    private static readonly HashSet<string> BandwidthModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "hd", "manual"
    };

    /// <summary>Campos numéricos (kbps) da política de teto de banda.</summary>
    private static readonly string[] BandwidthKbpsFields =
    [
        "ceilingKbps", "autoMinKbps", "autoMaxKbps", "autoStepKbps", "serverMaxKbps"
    ];

    internal static bool TryNormalizeRemoteSession(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (payload.TryGetProperty("consent", out var consent) && consent.ValueKind != JsonValueKind.Null)
        {
            if (consent.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'consent' must be an object or null.";
                return false;
            }

            if (consent.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                validationError = "field 'consent.required' must be boolean.";
                return false;
            }

            if (consent.TryGetProperty("mode", out var mode))
            {
                if (mode.ValueKind != JsonValueKind.String
                    || !PayloadJsonHelpers.TryReadString(mode, out var modeText)
                    || modeText is not ("off" or "notify" or "notifyonly" or "prompt"))
                {
                    validationError = "field 'consent.mode' must be one of: off, notify, prompt.";
                    return false;
                }
            }

            if (consent.TryGetProperty("timeoutSeconds", out var timeout) && timeout.ValueKind != JsonValueKind.Null)
            {
                if (timeout.ValueKind != JsonValueKind.Number
                    || !timeout.TryGetInt32(out var seconds)
                    || seconds < 10
                    || seconds > 600)
                {
                    validationError = "field 'consent.timeoutSeconds' must be between 10 and 600.";
                    return false;
                }
            }

            if (consent.TryGetProperty("timeoutAction", out var action))
            {
                if (action.ValueKind != JsonValueKind.String
                    || !PayloadJsonHelpers.TryReadString(action, out var actionText)
                    || actionText is not ("approve" or "deny"))
                {
                    validationError = "field 'consent.timeoutAction' must be one of: approve, deny.";
                    return false;
                }
            }

            if (consent.TryGetProperty("deadlineUtc", out var deadline) && deadline.ValueKind != JsonValueKind.Null)
            {
                if (deadline.ValueKind != JsonValueKind.String || !DateTime.TryParse(deadline.GetString(), out _))
                {
                    validationError = "field 'consent.deadlineUtc' must be an ISO-8601 date or null.";
                    return false;
                }
            }

            if (consent.TryGetProperty("message", out var message) && message.ValueKind != JsonValueKind.Null)
            {
                if (message.ValueKind != JsonValueKind.String || (message.GetString()?.Length ?? 0) > 500)
                {
                    validationError = "field 'consent.message' must be a string up to 500 chars.";
                    return false;
                }
            }
        }

        if (payload.TryGetProperty("banner", out var banner) && banner.ValueKind != JsonValueKind.Null)
        {
            if (banner.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'banner' must be an object or null.";
                return false;
            }

            if (banner.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                validationError = "field 'banner.enabled' must be boolean.";
                return false;
            }

            if (banner.TryGetProperty("text", out var text) && text.ValueKind != JsonValueKind.Null)
            {
                if (text.ValueKind != JsonValueKind.String || (text.GetString()?.Length ?? 0) > 500)
                {
                    validationError = "field 'banner.text' must be a string up to 500 chars.";
                    return false;
                }
            }
        }

        // runAs do terminal (opcional, aditivo): "user" (identidade do usuário
        // da sessão de console — default do agent) ou "system" (identidade do
        // agent). "inherit" é aceito por compatibilidade. O agent valida de
        // novo (defesa em profundidade) e faz fallback com aviso no log.
        if (payload.TryGetProperty("runAs", out var runAs) && runAs.ValueKind != JsonValueKind.Null)
        {
            if (runAs.ValueKind != JsonValueKind.String
                || !PayloadJsonHelpers.TryReadString(runAs, out var runAsText)
                || runAsText is not ("user" or "system" or "inherit"))
            {
                validationError = "field 'runAs' must be one of: user, system, inherit.";
                return false;
            }
        }

        // Política de TETO DE BANDA do stream de tela (opcional, aditivo):
        // { mode: "auto"|"hd"|"manual", ceilingKbps, autoMinKbps, autoMaxKbps,
        //   autoStepKbps, serverMaxKbps }. O agent valida de novo (defesa em
        // profundidade); aqui rejeitamos cedo para o operador receber erro claro
        // em vez de a sessão subir com uma política silenciosamente ignorada.
        if (payload.TryGetProperty("bandwidth", out var bandwidth) && bandwidth.ValueKind != JsonValueKind.Null)
        {
            if (bandwidth.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'bandwidth' must be an object or null.";
                return false;
            }

            if (bandwidth.TryGetProperty("mode", out var bwMode))
            {
                if (bwMode.ValueKind != JsonValueKind.String
                    || !PayloadJsonHelpers.TryReadString(bwMode, out var bwModeText)
                    || !BandwidthModes.Contains(bwModeText))
                {
                    validationError = "field 'bandwidth.mode' must be one of: auto, hd, manual.";
                    return false;
                }
            }

            // kbps: inteiro >= 0. 0 = sem limite (válido apenas em hd/manual).
            foreach (var field in BandwidthKbpsFields)
            {
                if (!bandwidth.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt32(out var kbps)
                    || kbps < 0)
                {
                    validationError = $"field 'bandwidth.{field}' must be a non-negative integer (kbps).";
                    return false;
                }
            }
        }

        normalizedPayload = payload.GetRawText();
        return true;
    }

    internal static bool TryNormalizeP2pPreload(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
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

    internal static bool TryNormalizeRemoteDebug(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!RemoteDebugActions.Contains(action))
        {
            validationError = "field 'action' must be one of: start, stop.";
            return false;
        }

        if (!PayloadJsonHelpers.TryGetRequiredGuid(payload, "sessionId", out var sessionId, out validationError))
            return false;

        var logLevel = "info";
        if (payload.TryGetProperty("logLevel", out var logLevelElement) && logLevelElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(logLevelElement, out var providedLogLevel))
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

        if (!PayloadJsonHelpers.TryGetRequiredString(stream, "natsSubject", out var natsSubject, out validationError))
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
            if (!PayloadJsonHelpers.TryGetRequiredString(payload, "expiresAtUtc", out var expiresRaw, out validationError))
                return false;

            if (!PayloadJsonHelpers.TryParseIsoUtc(expiresRaw, out var expiresAt))
            {
                validationError = "field 'expiresAtUtc' must be an ISO-8601 UTC datetime string.";
                return false;
            }

            expiresAtUtc = expiresAt.ToString("O");
        }
        else if (payload.TryGetProperty("expiresAtUtc", out var optionalExpires) && optionalExpires.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(optionalExpires, out var optionalRaw) || !PayloadJsonHelpers.TryParseIsoUtc(optionalRaw, out var optionalExpiresAt))
            {
                validationError = "field 'expiresAtUtc' must be an ISO-8601 UTC datetime string when provided.";
                return false;
            }

            expiresAtUtc = optionalExpiresAt.ToString("O");
        }

        // Teto absoluto da sessao (duracao maxima configurada na instalacao).
        string? maxExpiresAtUtc = null;
        if (payload.TryGetProperty("maxExpiresAtUtc", out var maxExpiresElement) && maxExpiresElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(maxExpiresElement, out var maxRaw) || !PayloadJsonHelpers.TryParseIsoUtc(maxRaw, out var maxExpiresAt))
            {
                validationError = "field 'maxExpiresAtUtc' must be an ISO-8601 UTC datetime string.";
                return false;
            }

            if (expiresAtUtc is not null && PayloadJsonHelpers.TryParseIsoUtc(expiresAtUtc, out var initialExpires) && maxExpiresAt < initialExpires)
            {
                validationError = "field 'maxExpiresAtUtc' must be greater than or equal to 'expiresAtUtc'.";
                return false;
            }

            maxExpiresAtUtc = maxExpiresAt.ToString("O");
        }

        // Bloco de liveness do canal de controle (opcional: defaults do agente).
        Dictionary<string, object?>? liveness = null;
        if (payload.TryGetProperty("liveness", out var livenessElement) && livenessElement.ValueKind == JsonValueKind.Object)
        {
            liveness = new Dictionary<string, object?>();
            foreach (var field in new[] { "pingIntervalSeconds", "missedPingsBeforeClose", "initialGraceSeconds", "keepAliveSeconds" })
            {
                if (!livenessElement.TryGetProperty(field, out var fieldElement) || fieldElement.ValueKind == JsonValueKind.Null)
                    continue;

                if (fieldElement.ValueKind != JsonValueKind.Number || !fieldElement.TryGetInt32(out var fieldValue) || fieldValue <= 0)
                {
                    validationError = $"field 'liveness.{field}' must be a positive integer.";
                    return false;
                }

                liveness[field] = fieldValue;
            }
        }

        var streamPayload = new Dictionary<string, object?>
        {
            ["natsSubject"] = natsSubject
        };

        // Subject UNICO de controle (ping/pong/setLevel). Opcional no payload:
        // quando ausente o agente deriva do natsSubject. Quando presente o
        // sufixo precisa ser exatamente o canonico.
        if (stream.TryGetProperty("natsControlSubject", out var controlElement) && controlElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(controlElement, out var controlSubject))
            {
                validationError = "field 'stream.natsControlSubject' must be a string.";
                return false;
            }

            if (!controlSubject.EndsWith(".remote-debug.control", StringComparison.Ordinal))
            {
                validationError = "field 'stream.natsControlSubject' must match the tenant-scoped remote-debug.control subject.";
                return false;
            }

            streamPayload["natsControlSubject"] = controlSubject;
        }

        var normalized = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["sessionId"] = sessionId,
            ["logLevel"] = logLevel,
            ["expiresAtUtc"] = expiresAtUtc,
            ["maxExpiresAtUtc"] = maxExpiresAtUtc,
            ["stream"] = streamPayload
        };

        if (liveness is not null)
            normalized["liveness"] = liveness;

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }
}
