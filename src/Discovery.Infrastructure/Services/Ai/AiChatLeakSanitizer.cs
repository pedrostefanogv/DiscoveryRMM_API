using System.Text.RegularExpressions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Sanitização de vazamentos de tool calls / marcações internas do LLM no
/// texto enviado ao usuário (agent).
///
/// 2026 (revisão do processo de chat): além dos padrões originais (DSML,
/// arrays de invokes, ação A2UI), adicionados: objeto único de invoke,
/// mensagens A2UI com version "v0.9" e blocos a2ui completos (fallback
/// para caminhos que não passam pelo A2uiExtractor).
/// </summary>
public static partial class AiChatLeakSanitizer
{
    [GeneratedRegex(@"<[｜|]DSML[｜|][a-z_]*>.*?</｜DSML｜[a-z_]*>", RegexOptions.Singleline)]
    private static partial Regex DsmlBlockRegex();

    [GeneratedRegex(@"</?[｜|]DSML[｜|][a-z_]*>")]
    private static partial Regex DsmlOrphanRegex();

    [GeneratedRegex(@"<invoke\s+name=""[^""]*"">.*?</invoke>", RegexOptions.Singleline)]
    private static partial Regex InvokeTagRegex();

    [GeneratedRegex(@"<parameter\s+name=""[^""]*"">.*?</parameter>", RegexOptions.Singleline)]
    private static partial Regex ParameterTagRegex();

    [GeneratedRegex(@"</?[｜|]?tool_invokes[｜|]?>")]
    private static partial Regex ToolInvokesTagRegex();

    // Blocos de código json (```json ... ``` ou ``` ... ```) com vazamento.
    [GeneratedRegex("```(?:json)?\\s*([\\s\\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    // Blocos ```a2ui ... ``` (C7): fallback para caminhos sem extractor.
    [GeneratedRegex("```a2ui[\\s\\S]*?(?:```|$)", RegexOptions.Singleline)]
    private static partial Regex A2uiFenceRegex();

    [GeneratedRegex(@"^\s*\[\s*\{\s*""name""\s*:")]
    private static partial Regex InvokeArrayRegex();

    // C6: objeto único de tool call emitido como texto.
    [GeneratedRegex(@"^\s*\{\s*""name""\s*:")]
    private static partial Regex InvokeObjectRegex();

    [GeneratedRegex(@"^\s*\{\s*""version""\s*:\s*""a2ui""")]
    private static partial Regex A2uiActionRegex();

    // C5: protocolo A2UI real usa version "v0.9" com verbos do catálogo.
    [GeneratedRegex(@"^\s*\{\s*""(createSurface|updateComponents|updateDataModel|deleteSurface)""")]
    private static partial Regex A2uiV09Regex();

    /// <summary>
    /// Remove vazamentos de tool calls e marcações internas do LLM de um
    /// texto de resposta. Retorna o texto limpo e um booleano indicando se
    /// algo foi removido.
    /// </summary>
    public static (string Clean, bool Removed) Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, false);

        var clean = text;
        var removed = false;

        // 1. Remove blocos DSML completos (com conteúdo).
        if (DsmlBlockRegex().IsMatch(clean))
        {
            clean = DsmlBlockRegex().Replace(clean, string.Empty);
            removed = true;
        }
        // 2. Remove tags DSML órfãs (stream cortado no meio).
        if (DsmlOrphanRegex().IsMatch(clean))
        {
            clean = DsmlOrphanRegex().Replace(clean, string.Empty);
            removed = true;
        }
        // 3. Remove invoke/parameter/tool_invokes soltos.
        foreach (var re in new[] { InvokeTagRegex(), ParameterTagRegex(), ToolInvokesTagRegex() })
        {
            if (re.IsMatch(clean))
            {
                clean = re.Replace(clean, string.Empty);
                removed = true;
            }
        }

        // 4. Remove blocos json com invokes/A2UI (o LLM "prometeu" executar
        // tools como texto).
        clean = JsonFenceRegex().Replace(clean, m =>
        {
            var body = m.Groups[1].Value.Trim();
            if (InvokeArrayRegex().IsMatch(body) || A2uiActionRegex().IsMatch(body) || A2uiV09Regex().IsMatch(body) || InvokeObjectRegex().IsMatch(body))
            {
                removed = true;
                return string.Empty;
            }
            return m.Value;
        });

        // 5. Remove blocos a2ui que sobraram (fallback — o caminho normal
        // extrai via A2uiExtractor antes da sanitização).
        if (A2uiFenceRegex().IsMatch(clean))
        {
            clean = A2uiFenceRegex().Replace(clean, string.Empty);
            removed = true;
        }

        if (!removed)
            return (text, false);

        // Limpeza final: colapsa linhas vazias em excesso das remoções.
        clean = clean.Trim();
        clean = Regex.Replace(clean, "\n{3,}", "\n\n");
        return (clean, true);
    }
}