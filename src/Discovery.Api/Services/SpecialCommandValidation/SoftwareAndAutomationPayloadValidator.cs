using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discovery.Api.Services;

internal static class SoftwareAndAutomationPayloadValidator
{
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

    /// <summary>
    /// Hive de um item de inicialização: HKLM, HKCU ou HKU:&lt;SID&gt; (itens de
    /// outros usuários). Restringir o formato evita que um agente comprometido
    /// monte caminhos de registro arbitrários via "hive".
    /// </summary>
    private static readonly Regex StartupItemHiveRegex = new(
        "^(HKLM|HKCU|HKU:S-1-[0-9-]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Payload de "executar sensores agora": { sensorIds?: string[], forceSync?: bool }.
    /// Normaliza guids ausentes/inválidos para lista vazia (= todos) e força o
    /// formato canônico para o agent.
    /// </summary>
    internal static bool TryNormalizeRunSensors(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        var sensorIds = new List<string>();
        if (payload.TryGetProperty("sensorIds", out var ids) && ids.ValueKind != JsonValueKind.Null)
        {
            if (ids.ValueKind != JsonValueKind.Array)
            {
                validationError = "field 'sensorIds' must be an array of guids.";
                return false;
            }

            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var parsed))
                {
                    validationError = "field 'sensorIds' must contain only valid guids.";
                    return false;
                }

                sensorIds.Add(parsed.ToString());
            }
        }

        var forceSync = false;
        if (payload.TryGetProperty("forceSync", out var force) && force.ValueKind != JsonValueKind.Null)
        {
            if (force.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                validationError = "field 'forceSync' must be boolean.";
                return false;
            }

            forceSync = force.GetBoolean();
        }

        normalizedPayload = JsonSerializer.Serialize(new { sensorIds, forceSync }, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de atualização de software: exige packageId e
    /// canonicaliza installationType/source para winget|chocolatey.
    /// </summary>
    internal static bool TryNormalizeSoftwareUpdate(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "packageId", out var packageId, out validationError))
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
            if (!PayloadJsonHelpers.TryReadString(typeElement, out var providedType))
            {
                validationError = "field 'installationType' must be a string.";
                return false;
            }

            var normalizedType = providedType.Trim().ToLowerInvariant();
            installationType = normalizedType.Contains("choco") ? "chocolatey" : "winget";
        }
        else if (payload.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(sourceElement, out var providedSource))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de instalação de um app do catálogo da loja: exige
    /// packageId e canonicaliza installationType/source para
    /// winget|chocolatey|custom (diferente de update/uninstall, o tipo custom é
    /// preservado — ele define a execução do instalador publicado).
    /// </summary>
    internal static bool TryNormalizeSoftwareInstall(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "packageId", out var packageId, out validationError))
            return false;

        packageId = packageId.Trim();
        if (packageId.Length == 0)
        {
            validationError = "field 'packageId' must be a non-empty string.";
            return false;
        }

        static string CanonicalizeSource(string value)
        {
            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Contains("choco"))
                return "chocolatey";
            if (normalized.Contains("custom"))
                return "custom";
            return "winget";
        }

        var installationType = "winget";
        if (payload.TryGetProperty("installationType", out var typeElement) && typeElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(typeElement, out var providedType))
            {
                validationError = "field 'installationType' must be a string.";
                return false;
            }

            installationType = CanonicalizeSource(providedType);
        }
        else if (payload.TryGetProperty("source", out var sourceElement) && sourceElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(sourceElement, out var providedSource))
            {
                validationError = "field 'source' must be a string.";
                return false;
            }

            installationType = CanonicalizeSource(providedSource);
        }

        var normalized = new Dictionary<string, object?>
        {
            ["packageId"] = packageId,
            ["installationType"] = installationType,
            ["source"] = installationType
        };

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    /// <summary>
    /// Normaliza o comando de desinstalação: exige name e canonicaliza
    /// installationType/source; packageId e os identificadores do registro são
    /// opcionais (o agent escolhe a melhor estratégia).
    /// </summary>
    internal static bool TryNormalizeSoftwareUninstall(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "name", out var name, out validationError))
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
            if (!PayloadJsonHelpers.TryReadString(element, out var value))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    internal static bool TryNormalizeStartupItem(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!EnableDisableActions.Contains(action))
        {
            validationError = "field 'action' must be one of: enable, disable.";
            return false;
        }

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "name", out var name, out validationError))
            return false;

        var itemType = "registry";
        if (payload.TryGetProperty("type", out var typeElement) && typeElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(typeElement, out var providedType))
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
            if (!PayloadJsonHelpers.TryReadString(sourceElement, out var providedSource))
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
            if (!PayloadJsonHelpers.TryReadString(hiveElement, out var providedHive))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }

    internal static bool TryNormalizeScheduledTask(
        JsonElement payload,
        out string normalizedPayload,
        out string validationError)
    {
        normalizedPayload = string.Empty;
        validationError = string.Empty;

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "action", out var action, out validationError))
            return false;

        action = action.ToLowerInvariant();
        if (!ScheduledTaskActions.Contains(action))
        {
            validationError = "field 'action' must be one of: enable, disable, run, delete, edit.";
            return false;
        }

        if (!PayloadJsonHelpers.TryGetRequiredString(payload, "taskName", out var taskName, out validationError))
            return false;

        string? taskPath = null;
        if (payload.TryGetProperty("taskPath", out var taskPathElement) && taskPathElement.ValueKind != JsonValueKind.Null)
        {
            if (!PayloadJsonHelpers.TryReadString(taskPathElement, out var providedPath))
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

            if (!PayloadJsonHelpers.TryGetRequiredString(editElement, "triggerType", out var triggerType, out validationError))
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
                if (!PayloadJsonHelpers.TryGetRequiredString(editElement, "time", out var time, out validationError))
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
                if (!PayloadJsonHelpers.TryReadString(actionPathElement, out var providedActionPath))
                {
                    validationError = "field 'edit.actionPath' must be a string.";
                    return false;
                }

                var actionPathValue = providedActionPath.Trim();
                if (actionPathValue.Length > 0)
                    edit["actionPath"] = actionPathValue;

                if (editElement.TryGetProperty("actionArgs", out var actionArgsElement) && actionArgsElement.ValueKind != JsonValueKind.Null)
                {
                    if (!PayloadJsonHelpers.TryReadString(actionArgsElement, out var providedActionArgs))
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

        normalizedPayload = JsonSerializer.Serialize(normalized, PayloadJsonHelpers.JsonOptions);
        return true;
    }
}
