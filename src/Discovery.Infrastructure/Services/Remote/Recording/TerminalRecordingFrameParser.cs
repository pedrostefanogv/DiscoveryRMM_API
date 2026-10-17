using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Core.DTOs;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>Resultado do parse de um frame de gravação de terminal.</summary>
public sealed record TerminalRecordingFrameParseResult(TerminalRecordingFrameDto? Frame, string? Error)
{
    public bool IsSuccess => Frame is not null;
    public static TerminalRecordingFrameParseResult Success(TerminalRecordingFrameDto frame) => new(frame, null);
    public static TerminalRecordingFrameParseResult Failure(string error) => new(null, error);
}

/// <summary>
/// Parser do JSON publicado pelo agent em <c>*.recording.term</c>.
/// Tolerante a campos opcionais/desconhecidos: kind não reconhecido vira
/// <c>unknown</c> (o frame ainda é persistido), mas JSON inválido ou sem os
/// campos obrigatórios é rejeitado sem lançar exceção.
/// </summary>
public static class TerminalRecordingFrameParser
{
    private static readonly HashSet<string> KnownKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "output", "ready", "resize", "exit", "error"
    };

    public static TerminalRecordingFrameParseResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TerminalRecordingFrameParseResult.Failure("payload vazio");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            return TerminalRecordingFrameParseResult.Failure($"JSON inválido: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return TerminalRecordingFrameParseResult.Failure("payload não é um objeto JSON");

            if (!root.TryGetProperty("seq", out var seqElement)
                || !seqElement.TryGetInt64(out var seq)
                || seq < 0)
            {
                return TerminalRecordingFrameParseResult.Failure("campo 'seq' ausente ou inválido");
            }

            if (!root.TryGetProperty("timestampMs", out var timestampElement)
                || !timestampElement.TryGetInt64(out var timestampMs)
                || timestampMs < 0)
            {
                return TerminalRecordingFrameParseResult.Failure("campo 'timestampMs' ausente ou inválido");
            }

            var data = string.Empty;
            if (root.TryGetProperty("data", out var dataElement))
            {
                if (dataElement.ValueKind != JsonValueKind.String)
                    return TerminalRecordingFrameParseResult.Failure("campo 'data' deve ser string");
                data = dataElement.GetString() ?? string.Empty;
            }

            var kind = "output";
            if (root.TryGetProperty("kind", out var kindElement))
            {
                if (kindElement.ValueKind != JsonValueKind.String)
                    return TerminalRecordingFrameParseResult.Failure("campo 'kind' deve ser string");
                kind = kindElement.GetString()?.Trim().ToLowerInvariant() ?? "output";
                if (kind.Length == 0)
                    kind = "output";
            }

            var isKnownKind = KnownKinds.Contains(kind);
            var normalizedKind = isKnownKind ? kind : "unknown";

            var frame = new TerminalRecordingFrameDto(
                data,
                seq,
                timestampMs,
                normalizedKind,
                ReadOptionalInt32(root, "exitCode"),
                ReadOptionalInt32(root, "cols"),
                ReadOptionalInt32(root, "rows"),
                ReadOptionalString(root, "backend"),
                ReadOptionalString(root, "shellKind"),
                isKnownKind);

            return TerminalRecordingFrameParseResult.Success(frame);
        }
    }

    /// <summary>
    /// Monta a linha NDJSON preservando todos os campos publicados pelo agent e
    /// acrescentando o sessionId (o arquivo é por sessão, mas o campo explícito
    /// facilita o consumo do download).
    /// </summary>
    public static string BuildNdjsonLine(string rawJson, Guid sessionId)
    {
        try
        {
            if (JsonNode.Parse(rawJson) is JsonObject obj)
            {
                obj["sessionId"] = sessionId.ToString("D");
                return obj.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Fallback: o worker só chama isto após o parse; se algo mudou,
            // persiste o payload original em vez de perder o frame.
        }

        return rawJson;
    }

    private static int? ReadOptionalInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return null;

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return null;

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
