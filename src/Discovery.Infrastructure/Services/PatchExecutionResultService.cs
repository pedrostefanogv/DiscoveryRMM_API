using System.Text.Json;
using Discovery.Core.Enums;
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
        var failureCode = (int?)null;
        var failureMessage = errorMessage;

        if (parsed is not null)
        {
            installed.AddRange(parsed.Installed.Where(updateKeys.Contains));
            failed.AddRange(parsed.Failed.Where(updateKeys.Contains));

            var missing = updateKeys
                .Where(key => !installed.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                              !failed.Contains(key, StringComparer.OrdinalIgnoreCase))
                .ToList();
            // "notFound" = update saiu do catálogo entre aprovação e instalação.
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

        if (failed.Count > 0)
        {
            var message = string.IsNullOrWhiteSpace(failureMessage)
                ? "instalação não confirmada pelo agente"
                : failureMessage.Trim();
            await patchRepo.UpdateInstallStateAsync(
                command.AgentId, failed, PatchUpdateStatus.Failed, failureCode, message, ct);
        }

        await resolver.RecomputeForAgentAsync(command.AgentId, ct);
        await resolver.RefreshSnapshotCountsAsync(command.AgentId, ct);

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

            return new ParsedOutput(installed, failed, rebootRequired);
        }
        catch (JsonException)
        {
            return null;
        }
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
