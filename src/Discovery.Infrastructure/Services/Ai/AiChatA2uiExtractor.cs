using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Extrai mensagens A2UI (Agent-to-User Interface) do conteúdo gerado pelo LLM.
///
/// O LLM é instruído (via system prompt) a emitir interfaces A2UI dentro de um
/// fenced code block com linguagem `a2ui`:
///
/// ```a2ui
/// {"version":"v0.9","createSurface":{...}}
/// {"version":"v0.9","updateComponents":{...}}
/// ```
///
/// Cada linha do bloco é uma mensagem A2UI (JSONL). O helper:
///   - Detecta e extrai essas mensagens;
///   - Remove o bloco do texto visível ao usuário (o texto restante segue o
///     fluxo markdown normal);
///   - Valida minimamente que cada linha é um JSON com "version" e um dos
///     verbos A2UI (createSurface/updateComponents/updateDataModel/deleteSurface).
///
/// Isso mantém o A2UI "secure by design": o renderer só processa mensagens
/// declarativas do catálogo aprovado, nunca código executável.
/// </summary>
public static class AiChatA2uiExtractor
{
    // C8: abertura de bloco tolerante (espacos e maiusculas variando).
    private static readonly Regex A2uiOpenRegex = new("^```\\s*a2ui\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] A2uiVerbs =
    {
        "createSurface", "updateComponents", "updateDataModel", "deleteSurface"
    };

    /// <summary>
    /// Tenta extrair mensagens A2UI do conteúdo. Retorna o conteúdo "limpo"
    /// (sem os blocos a2ui) e a lista de mensagens A2UI válidas.
    /// </summary>
    public static (string CleanContent, List<string> A2uiMessages) Extract(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (content ?? string.Empty, new List<string>());

        var clean = new StringBuilder(content.Length);
        var surfaceEmitted = false; // C2: no máximo 1 surface por resposta
        var discardBlock = false;
        var messages = new List<string>();

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inA2uiBlock = false;
        var blockBuffer = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (!inA2uiBlock)
            {
                // Abre bloco: ```a2ui (com ou sem trailing spaces)
                if (A2uiOpenRegex.IsMatch(line))
                {
                    inA2uiBlock = true;
                    blockBuffer.Clear();
                    continue;
                }
                clean.Append(rawLine).Append('\n');
                continue;
            }

            // Fecha bloco: ```
            if (line.StartsWith("```"))
            {
                inA2uiBlock = false;
                if (discardBlock)
                {
                    continue; // C2: surface extra descartada
                }
                var blockMsgs = ParseBlock(blockBuffer.ToString()).ToList();
                // C2: no maximo 1 createSurface por resposta - updateComponents/
                // updateDataModel/deleteSurface da MESMA surface sao permitidos.
                var hasCreate = blockMsgs.Any(m => m.Contains("createSurface"));
                if (hasCreate && surfaceEmitted)
                {
                    continue; // C2: segunda surface descartada
                }
                if (hasCreate) surfaceEmitted = true;
                messages.AddRange(blockMsgs);
                continue;
            }
            blockBuffer.Append(rawLine).Append('\n');
        }

        // Bloco não fechado: processa o que sobrou
        if (inA2uiBlock)
        {
            foreach (var msg in ParseBlock(blockBuffer.ToString()))
                messages.Add(msg);
        }

        return (clean.ToString().TrimEnd('\n'), messages);
    }

    private static IEnumerable<string> ParseBlock(string block)
    {
        foreach (var rawLine in block.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (IsValidA2uiMessage(line))
                yield return line;
        }
    }

    private static bool IsValidA2uiMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("version", out var versionProp))
                return false;
            var version = versionProp.GetString();
            if (string.IsNullOrWhiteSpace(version)) return false;

            foreach (var verb in A2uiVerbs)
            {
                if (doc.RootElement.TryGetProperty(verb, out _))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
