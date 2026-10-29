using System.Text.Json;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Interpreta o resultado do comando de instalação de patches e atualiza as
/// linhas de inventário (Installed/Failed) + snapshot do agente.
///
/// O agente devolve JSON com installed[]/failed[]/notFound[]; quando o output
/// não é JSON (agente antigo ou saída truncada), cai para o exitCode do comando.
/// </summary>
public sealed class PatchExecutionResultService(
    ICommandRepository commandRepo,
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchEventPublisher events,
    ILogger<PatchExecutionResultService>? logger = null) : IPatchExecutionResultService
{
    public async Task HandleCommandResultAsync(
        Guid commandId,
        int exitCode,
        string? output,
        string? errorMessage,
        DateTime completedAt,
        CancellationToken ct = default)
    {
        var command = await commandRepo.GetByIdAsync(commandId);
        if (command is null || command.CommandType != CommandType.WindowsUpdate)
            return;

        // Comando de política no SO: o agente devolve o backup do estado anterior;
        // guardamos em patch_os_states para permitir rollback fiel.
        if (TryExtractPolicyBackup(command.Payload, output, out var policyBackup))
        {
            await patchRepo.UpdateOsStateBackupAsync(command.AgentId, policyBackup, ct);
        }

        var updateKeys = ExtractUpdateKeys(command.Payload);
        if (updateKeys.Count == 0)
            return;

        var parsed = ParseOutput(output);
        var success = exitCode == 0;

        var installed = new List<string>();
        var failed = new List<string>();
        // WU-07: skipped NÃO é falha. "already_installed" confirma o estado
        // desejado; "not_offered" = update saiu do catálogo (superseded/expirado).
        var alreadyInstalled = new List<string>();
        var notOffered = new List<string>();
        var failureCode = (int?)null;
        var failureMessage = errorMessage;
        var errorCode = (string?)null;

        if (parsed is not null)
        {
            installed.AddRange(parsed.Installed.Where(updateKeys.Contains));
            failed.AddRange(parsed.Failed.Where(updateKeys.Contains));
            errorCode = parsed.ErrorCode;

            foreach (var (key, reason) in parsed.Skipped.Where(item => updateKeys.Contains(item.Key)))
            {
                if (string.Equals(reason, "already_installed", StringComparison.Ordinal))
                    alreadyInstalled.Add(key);
                else
                    notOffered.Add(key);
            }

            var missing = updateKeys
                .Where(key => !installed.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                              !failed.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                              !alreadyInstalled.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                              !notOffered.Contains(key, StringComparer.OrdinalIgnoreCase))
                .ToList();
            // Agente antigo não envia "skipped": o alvo não citado continua
            // contando como falha (comportamento anterior, sem esconder problema).
            foreach (var key in missing)
                failed.Add(key);

            if (parsed.RebootRequired)
                await patchRepo.UpdateAgentPendingRebootAsync(
                    command.AgentId, true, ["windows_update"], completedAt, ct);
        }
        else if (success)
        {
            installed.AddRange(updateKeys);
        }
        else
        {
            failed.AddRange(updateKeys);
            failureCode = exitCode;
        }

        if (installed.Count > 0)
        {
            await patchRepo.UpdateInstallStateAsync(
                command.AgentId, installed, PatchUpdateStatus.Installed, null, null, ct);
        }

        if (alreadyInstalled.Count > 0)
        {
            // Já estava instalado: o estado desejado é o atual.
            await patchRepo.UpdateInstallStateAsync(
                command.AgentId, alreadyInstalled, PatchUpdateStatus.Installed, null, null, ct);
        }

        if (notOffered.Count > 0)
        {
            // Saiu do catálogo entre a aprovação e a execução.
            await patchRepo.UpdateInstallStateAsync(
                command.AgentId, notOffered, PatchUpdateStatus.Superseded, null,
                "atualização não é mais oferecida pelo Windows Update", ct);
        }

        if (failed.Count > 0)
        {
            var message = string.IsNullOrWhiteSpace(failureMessage)
                ? "instalação não confirmada pelo agente"
                : failureMessage.Trim();
            // WU-08: classificação determinística + backoff (só transient re-tenta).
            var failureClass = PatchFailureClassifier.Classify(errorCode, message, failureCode);
            await patchRepo.RecordInstallFailureAsync(
                command.AgentId, failed, failureClass, failureCode, message, ct);
            logger?.LogWarning(
                "Patch install failed: command={CommandId} agent={AgentId} class={FailureClass} errorCode={ErrorCode} keys={Count}",
                commandId, command.AgentId, failureClass, errorCode, failed.Count);
        }

        await resolver.RecomputeForAgentAsync(command.AgentId, ct);
        await resolver.RefreshSnapshotCountsAsync(command.AgentId, ct);

        var agent = await agentRepo.GetByIdAsync(command.AgentId);
        var site = agent is null ? null : await siteRepo.GetByIdAsync(agent.SiteId);
        await events.PublishInstallCompletedAsync(
            command.AgentId,
            agent?.Hostname ?? string.Empty,
            site?.ClientId,
            agent?.SiteId,
            installed.Count,
            failed.Count,
            parsed?.RebootRequired ?? false,
            ct);

        logger?.LogInformation(
            "Patch install result: command={CommandId} agent={AgentId} exit={ExitCode} installed={Installed} failed={Failed}",
            commandId, command.AgentId, exitCode, installed.Count, failed.Count);
    }

    private static bool TryExtractPolicyBackup(string? payload, string? output, out string backupJson)
    {
        backupJson = string.Empty;
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(output))
            return false;

        try
        {
            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object ||
                !payloadDocument.RootElement.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.String ||
                !string.Equals(actionElement.GetString(), "policy", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var json = output.Trim();
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
                return false;

            using var outputDocument = JsonDocument.Parse(json[start..(end + 1)]);
            if (outputDocument.RootElement.ValueKind != JsonValueKind.Object ||
                !outputDocument.RootElement.TryGetProperty("backup", out var backupElement) ||
                backupElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            backupJson = backupElement.GetRawText();
            return backupJson.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractUpdateKeys(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var keys = new List<string>();
            if (document.RootElement.TryGetProperty("updates", out var updates) &&
                updates.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in updates.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("updateKey", out var keyElement) &&
                        keyElement.ValueKind == JsonValueKind.String)
                    {
                        var key = keyElement.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(key))
                            keys.Add(key);
                    }
                }
            }

            if (keys.Count == 0 &&
                document.RootElement.TryGetProperty("updateKey", out var single) &&
                single.ValueKind == JsonValueKind.String)
            {
                var key = single.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    keys.Add(key);
            }

            return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ParsedOutput(
        IReadOnlyList<string> Installed,
        IReadOnlyList<string> Failed,
        IReadOnlyList<(string Key, string Reason)> Skipped,
        string? ErrorCode,
        bool RebootRequired);

    private static ParsedOutput? ParseOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var json = output.Trim();
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        json = json[start..(end + 1)];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var installed = ReadKeyArray(document.RootElement, "installed");
            var failed = ReadKeyArray(document.RootElement, "failed");
            var notFound = ReadKeyArray(document.RootElement, "notFound");
            failed.AddRange(notFound);
            // WU-07: skipped não é falha. O agente informa o motivo estável.
            var skipped = ReadSkippedArray(document.RootElement);
            // WU-06: motivo estruturado do preflight (disk_space, service_stopped…).
            var errorCode = ReadStringProperty(document.RootElement, "errorCode");

            var rebootRequired = false;
            if (document.RootElement.TryGetProperty("rebootRequired", out var rebootElement))
            {
                rebootRequired = rebootElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.String => bool.TryParse(rebootElement.GetString(), out var parsed) && parsed,
                    _ => false
                };
            }

            return new ParsedOutput(installed, failed, skipped, errorCode, rebootRequired);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStringProperty(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static List<(string Key, string Reason)> ReadSkippedArray(JsonElement root)
    {
        var skipped = new List<(string Key, string Reason)>();
        if (!root.TryGetProperty("skipped", out var element) || element.ValueKind != JsonValueKind.Array)
            return skipped;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("updateKey", out var keyElement) ||
                keyElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = keyElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var reason = item.TryGetProperty("skipReason", out var reasonElement) &&
                         reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;
            skipped.Add((key, reason));
        }

        return skipped;
    }

    private static List<string> ReadKeyArray(JsonElement root, string property)
    {
        var keys = new List<string>();
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
            return keys;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var raw = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                    keys.Add(raw);
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("updateKey", out var keyElement) &&
                keyElement.ValueKind == JsonValueKind.String)
            {
                var raw = keyElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                    keys.Add(raw);
            }
        }

        return keys;
    }
}
