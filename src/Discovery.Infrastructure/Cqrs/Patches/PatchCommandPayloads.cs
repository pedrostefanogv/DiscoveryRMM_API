using System.Text.Json;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>
/// Monta os payloads JSON enviados ao agente (contrato camelCase). O agente
/// revalida tudo; aqui a única responsabilidade é montar um payload normalizado.
/// </summary>
public static class PatchCommandPayloads
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Scan(string reason) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["action"] = "scan",
            ["reason"] = reason
        }, Options);

    public static string Install(
        IEnumerable<AgentWindowsUpdate> updates,
        bool force,
        string? postAction,
        int ttlSeconds = 1800)
    {
        var items = updates.Select(update => new Dictionary<string, object?>
        {
            ["updateKey"] = update.UpdateKey,
            ["updateId"] = update.UpdateId,
            ["revision"] = update.Revision,
            ["kbIds"] = PatchJson.ParseStringArray(update.KbIds)
        }).ToList();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = "install",
            ["updates"] = items,
            ["force"] = force,
            ["postAction"] = NormalizePostAction(postAction),
            ["ttlSeconds"] = ttlSeconds,
            ["issuedAtUtc"] = DateTime.UtcNow.ToString("O")
        }, Options);
    }

    public static string Hide(IEnumerable<string> updateKeys, bool hidden) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["action"] = hidden ? "hide" : "unhide",
            ["updateKeys"] = updateKeys.ToList()
        }, Options);

    public static string PolicyRead() => JsonSerializer.Serialize(
        new Dictionary<string, object?> { ["action"] = "policy-read" }, Options);

    public static string PolicyApply(object settings) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["action"] = "policy",
            ["settings"] = settings
        }, Options);

    public static string PolicyRollback(string backupJson) => JsonSerializer.Serialize(
        new Dictionary<string, object?>
        {
            ["action"] = "policy-rollback",
            ["backup"] = JsonSerializer.Deserialize<JsonElement>(backupJson, Options)
        }, Options);

    public static string NormalizePostAction(string? postAction)
        => postAction?.Trim().ToLowerInvariant() switch
        {
            "restart" => "restart",
            "shutdown" => "shutdown",
            _ => "none"
        };
}
