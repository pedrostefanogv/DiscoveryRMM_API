using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.DTOs;

/// <summary>
/// Um passo de runbook executável anexado a uma TicketMacro (M242).
/// Executa na ordem: dispara o AutomationScript na máquina vinculada ao
/// chamado pelo mesmo caminho do "run now" de automação.
/// </summary>
public sealed record RunbookStep(
    int Order,
    Guid AutomationScriptId,
    string? ArgsJson = null,
    string? EvidenceLabel = null,
    bool StopOnFailure = false,
    int? TimeoutSeconds = null);


public static class RunbookStepList
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<RunbookStep> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RunbookStep>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string ToJson(IReadOnlyList<RunbookStep> steps)
        => JsonSerializer.Serialize(steps, JsonOptions);
}
