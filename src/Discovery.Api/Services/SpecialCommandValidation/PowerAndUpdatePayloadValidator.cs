using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discovery.Api.Services;

internal static class PowerAndUpdatePayloadValidator
{
    private static readonly HashSet<string> UpdateActions = new(StringComparer.Ordinal)
    {
        "check-update",
        "install",
        "rollback"
    };

    /// <summary>
    /// Verbos aceitos no comando genérico de energia (CommandType.PowerAction).
    /// Qualquer novo modo precisa entrar AQUI e ser implementado no agent
    /// (handleDirectPowerAction / executeSystemPowerAction).
    /// </summary>
    private static readonly HashSet<string> PowerActionVerbs = new(StringComparer.Ordinal)
    {
        // Reinicia direto no setup do firmware (BIOS/UEFI) — shutdown /r /fw.
        // Só funciona em boot UEFI: o agent valida e falha de forma tipada.
        "restart-firmware",
        // Reinicia nas Opções Avançadas de inicialização (menu do WinRE, onde
        // ficam modo de segurança e reparo) — shutdown /r /o.
        "restart-advanced",
        // Cancela um desligamento/reinício pendente — shutdown /a.
        "cancel",
        // Bloqueia a sessão interativa (LockWorkStation). Reversível.
        "lock"
    };

    private static readonly Regex SemVerRegex = new(
        "^\\d+\\.\\d+\\.\\d+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Chave canônica de atualização aceita nos payloads de patch:
    /// guid:&lt;uuid&gt;:&lt;rev&gt; (revisão do WUA) ou kb:KBxxxx (KB puro).
    /// </summary>
    private static readonly Regex PatchUpdateKeyRegex = new(
        "^(guid:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:[0-9]{1,10}|kb:KB[0-9]{4,10})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PatchKbIdRegex = new(
        "^KB[0-9]{4,10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MacAddressRegex = new(
        "^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool TryNormalizeUpdate(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
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
            if (!PayloadJsonHelpers.TryReadString(versionElement, out var providedVersion))
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
            if (!PayloadJsonHelpers.TryReadString(urlElement, out var providedUrl))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    internal static bool TryNormalizeRestart(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
        => TryNormalizePowerAction(payload, defaultDelaySeconds: 15, out normalizedPayload, out validationError);

    internal static bool TryNormalizeShutdown(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
        => TryNormalizePowerAction(payload, defaultDelaySeconds: 30, out normalizedPayload, out validationError);

    /// <summary>
    /// Valida/normaliza o comando genérico de energia (CommandType.PowerAction).
    /// Diferente de restart/shutdown, o atraso pode ser 0 (execução imediata) —
    /// é o caso do reinício direto no firmware.
    /// </summary>
    internal static bool TryNormalizePowerActionCommand(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!PowerActionVerbs.Contains(action))
        {
            validationError = "field 'action' must be one of: " + string.Join(", ", PowerActionVerbs) + ".";
            return false;
        }

        var delaySeconds = 0;
        if (payload.TryGetProperty("delaySeconds", out var delayElement) && delayElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadNonNegativeInt(delayElement, out delaySeconds))
            {
                validationError = "field 'delaySeconds' must be a non-negative integer.";
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

        // notifyUser=false por padrão: o poweraction não usa o contador do
        // restart/shutdown; o verbo decide o próprio fluxo.
        var notifyUser = false;
        if (payload.TryGetProperty("notifyUser", out var notifyElement) && notifyElement.ValueKind != JsonValueKind.Null)
        {
            if (notifyElement.ValueKind == JsonValueKind.True || notifyElement.ValueKind == JsonValueKind.False)
            {
                notifyUser = notifyElement.GetBoolean();
            }
            else
            {
                validationError = "field 'notifyUser' must be a boolean.";
                return false;
            }
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var msgElement) && msgElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(msgElement, out var providedMessage))
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
            ["action"] = action,
            ["delaySeconds"] = delaySeconds,
            ["force"] = force,
            ["notifyUser"] = notifyUser,
            ["message"] = message
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    private static bool TryNormalizePowerAction(
        JsonElement payload,
        int defaultDelaySeconds,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        var delaySeconds = defaultDelaySeconds;
        if (payload.TryGetProperty("delaySeconds", out var delayElement) && delayElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadPositiveInt(delayElement, out delaySeconds))
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

        // notifyUser: exibe no agent o aviso Fluent com contador antes de agir.
        var notifyUser = true;
        if (payload.TryGetProperty("notifyUser", out var notifyElement) && notifyElement.ValueKind != JsonValueKind.Null)
        {
            if (notifyElement.ValueKind == JsonValueKind.True || notifyElement.ValueKind == JsonValueKind.False)
            {
                notifyUser = notifyElement.GetBoolean();
            }
            else
            {
                validationError = "field 'notifyUser' must be a boolean.";
                return false;
            }
        }

        string? message = null;
        if (payload.TryGetProperty("message", out var msgElement) && msgElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(msgElement, out var providedMessage))
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
            ["notifyUser"] = notifyUser,
            ["message"] = message
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    internal static bool TryNormalizeWakeOnLan(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "macAddress", out var macAddress, out validationError))
            return false;

        if (!MacAddressRegex.IsMatch(macAddress))
        {
            validationError = "field 'macAddress' must be a valid MAC address (e.g., 00:11:22:33:44:55).";
            return false;
        }

        string? broadcastAddress = null;
        if (payload.TryGetProperty("broadcastAddress", out var broadcastElement) && broadcastElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(broadcastElement, out var providedBroadcast))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de descomissionamento remoto. O payload pode ser
    /// vazio (a origem é o próprio painel); "requestedBy"/"reason" são campos
    /// de auditoria opcionais e são descartados quando vazios.
    /// </summary>
    internal static bool TryNormalizeDecommission(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        string? OptionalString(string property)
        {
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
                return null;
            if (!PayloadJsonHelpers.TryReadString(element, out var value))
                return null;
            return value.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }

        // QuandoWritingNull não é honrado para valores de dicionário — os
        // campos opcionais só entram quando realmente têm conteúdo.
        var normalized = new Dictionary<string, object?>();
        if (OptionalString("requestedBy") is { } requestedBy)
            normalized["requestedBy"] = requestedBy;
        if (OptionalString("reason") is { } reason)
            normalized["reason"] = reason;

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o payload do comando de patches (windowsupdate). Validação
    /// estrita: ação em allowlist, chaves canônicas, máximo de 100 updates,
    /// postAction allowlist e TTL limitado. O agente revalida tudo de novo.
    /// </summary>
    internal static bool TryNormalizeWindowsUpdate(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var actionRaw, out validationError))
            return false;

        var action = actionRaw.Trim().ToLowerInvariant();
        if (action is not ("scan" or "install" or "hide" or "unhide" or "policy" or "policy-rollback" or "policy-read"))
        {
            validationError = "field 'action' must be one of: scan, install, hide, unhide, policy, policy-rollback, policy-read.";
            return false;
        }

        var normalized = new Dictionary<string, object?> { ["action"] = action };

        if (action is "hide" or "unhide")
        {
            if (!payload.TryGetProperty("updateKeys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array)
            {
                validationError = "field 'updateKeys' must be an array of update keys.";
                return false;
            }

            var keys = new List<string>();
            foreach (var item in keysElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    !PayloadJsonHelpers.TryReadString(item, out var key) ||
                    !PatchUpdateKeyRegex.IsMatch(key.Trim()))
                {
                    validationError = "field 'updateKeys' contains an invalid update key.";
                    return false;
                }
                keys.Add(key.Trim());
            }

            if (keys.Count is 0 or > 100)
            {
                validationError = "field 'updateKeys' must contain between 1 and 100 keys.";
                return false;
            }

            normalized["updateKeys"] = keys;
        }
        else if (action == "install")
        {
            if (!payload.TryGetProperty("updates", out var updatesElement) || updatesElement.ValueKind != JsonValueKind.Array)
            {
                validationError = "field 'updates' must be an array.";
                return false;
            }

            var updates = new List<Dictionary<string, object?>>();
            foreach (var item in updatesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    validationError = "field 'updates' must contain objects.";
                    return false;
                }

                if (!item.TryGetProperty("updateKey", out var keyElement) ||
                    keyElement.ValueKind != JsonValueKind.String ||
                    !PayloadJsonHelpers.TryReadString(keyElement, out var updateKey) ||
                    !PatchUpdateKeyRegex.IsMatch(updateKey.Trim()))
                {
                    validationError = "field 'updates[].updateKey' is invalid.";
                    return false;
                }

                var entry = new Dictionary<string, object?> { ["updateKey"] = updateKey.Trim() };

                if (item.TryGetProperty("updateId", out var updateIdElement) &&
                    updateIdElement.ValueKind != JsonValueKind.Null)
                {
                    if (updateIdElement.ValueKind != JsonValueKind.String ||
                        !PayloadJsonHelpers.TryReadString(updateIdElement, out var updateIdRaw) ||
                        !Guid.TryParse(updateIdRaw, out var updateId))
                    {
                        validationError = "field 'updates[].updateId' must be a GUID.";
                        return false;
                    }
                    entry["updateId"] = updateId.ToString("D").ToLowerInvariant();
                }

                if (item.TryGetProperty("revision", out var revisionElement) &&
                    revisionElement.ValueKind != JsonValueKind.Null)
                {
                    if (revisionElement.ValueKind != JsonValueKind.Number ||
                        !revisionElement.TryGetInt32(out var revision) ||
                        revision < 0)
                    {
                        validationError = "field 'updates[].revision' must be a non-negative integer.";
                        return false;
                    }
                    entry["revision"] = revision;
                }

                if (item.TryGetProperty("kbIds", out var kbElement) && kbElement.ValueKind != JsonValueKind.Null)
                {
                    if (kbElement.ValueKind != JsonValueKind.Array)
                    {
                        validationError = "field 'updates[].kbIds' must be an array.";
                        return false;
                    }

                    var kbIds = new List<string>();
                    foreach (var kbItem in kbElement.EnumerateArray())
                    {
                        if (kbItem.ValueKind != JsonValueKind.String ||
                            !PayloadJsonHelpers.TryReadString(kbItem, out var kbRaw))
                        {
                            validationError = "field 'updates[].kbIds' contains an invalid KB id.";
                            return false;
                        }

                        // Aceita "5034441", "KB5034441" e "KB 5034441"; normaliza
                        // para KBxxxx antes de validar.
                        var normalizedKb = kbRaw.Trim().ToUpperInvariant().Replace(" ", string.Empty);
                        if (!normalizedKb.StartsWith("KB", StringComparison.Ordinal))
                            normalizedKb = "KB" + normalizedKb;

                        if (!PatchKbIdRegex.IsMatch(normalizedKb))
                        {
                            validationError = "field 'updates[].kbIds' contains an invalid KB id.";
                            return false;
                        }

                        if (!kbIds.Contains(normalizedKb, StringComparer.Ordinal))
                            kbIds.Add(normalizedKb);
                    }
                    entry["kbIds"] = kbIds;
                }

                updates.Add(entry);
            }

            if (updates.Count is 0 or > 100)
            {
                validationError = "field 'updates' must contain between 1 and 100 updates.";
                return false;
            }
            normalized["updates"] = updates;

            if (payload.TryGetProperty("force", out var forceElement) && forceElement.ValueKind != JsonValueKind.Null)
            {
                if (forceElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    validationError = "field 'force' must be a boolean.";
                    return false;
                }
                normalized["force"] = forceElement.GetBoolean();
            }

            var postAction = "none";
            if (payload.TryGetProperty("postAction", out var postActionElement) &&
                postActionElement.ValueKind != JsonValueKind.Null)
            {
                if (postActionElement.ValueKind != JsonValueKind.String ||
                    !PayloadJsonHelpers.TryReadString(postActionElement, out var postActionRaw))
                {
                    validationError = "field 'postAction' must be a string.";
                    return false;
                }
                postAction = postActionRaw.Trim().ToLowerInvariant();
                if (postAction is not ("none" or "restart" or "shutdown"))
                {
                    validationError = "field 'postAction' must be one of: none, restart, shutdown.";
                    return false;
                }
            }
            normalized["postAction"] = postAction;

            var ttlSeconds = 1800;
            if (payload.TryGetProperty("ttlSeconds", out var ttlElement) && ttlElement.ValueKind != JsonValueKind.Null)
            {
                if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out ttlSeconds))
                {
                    validationError = "field 'ttlSeconds' must be an integer.";
                    return false;
                }
                ttlSeconds = Math.Clamp(ttlSeconds, 60, 7200);
            }
            normalized["ttlSeconds"] = ttlSeconds;

            var issuedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (payload.TryGetProperty("issuedAtUtc", out var issuedElement) &&
                issuedElement.ValueKind != JsonValueKind.Null)
            {
                if (issuedElement.ValueKind != JsonValueKind.String ||
                    !PayloadJsonHelpers.TryReadString(issuedElement, out var issuedRaw) ||
                    !DateTimeOffset.TryParse(issuedRaw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var issuedAtUtc))
                {
                    validationError = "field 'issuedAtUtc' must be an ISO-8601 timestamp.";
                    return false;
                }
                issuedAt = issuedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            }
            normalized["issuedAtUtc"] = issuedAt;
        }
        else if (action == "policy")
        {
            if (!payload.TryGetProperty("settings", out var settingsElement) ||
                settingsElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'settings' must be an object.";
                return false;
            }

            var settings = new Dictionary<string, object?>();
            foreach (var field in new[]
                     {
                         "noAutoUpdate", "auOptions", "deferQualityUpdates",
                         "deferQualityUpdatesPeriodInDays", "pauseQualityUpdates"
                     })
            {
                if (!settingsElement.TryGetProperty(field, out var valueElement) ||
                    valueElement.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                if (valueElement.ValueKind != JsonValueKind.Number ||
                    !valueElement.TryGetUInt32(out var value))
                {
                    validationError = $"field 'settings.{field}' must be a non-negative integer.";
                    return false;
                }
                settings[field] = value;
            }

            if (settingsElement.TryGetProperty("pauseQualityUpdatesStartTime", out var pauseElement) &&
                pauseElement.ValueKind != JsonValueKind.Null)
            {
                if (pauseElement.ValueKind != JsonValueKind.String ||
                    !PayloadJsonHelpers.TryReadString(pauseElement, out var pauseRaw) ||
                    pauseRaw.Length > 64)
                {
                    validationError = "field 'settings.pauseQualityUpdatesStartTime' must be an ISO-8601 string.";
                    return false;
                }
                settings["pauseQualityUpdatesStartTime"] = pauseRaw.Trim();
            }

            normalized["settings"] = settings;
        }
        else if (action == "policy-rollback")
        {
            if (!payload.TryGetProperty("backup", out var backupElement) ||
                backupElement.ValueKind != JsonValueKind.Object)
            {
                validationError = "field 'backup' must be an object.";
                return false;
            }
            normalized["backup"] = backupElement.GetRawText();
        }

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }
}
