using System.Text.RegularExpressions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Sanitização de vazamentos de tool calls / marcações internas do LLM no
/// texto enviado ao usuário (agent).
///
/// Contexto (2026-08-30): o modelo às vezes emite tool calls como TEXTO em
/// vez de function call nativa, em três formatos observados:
///
///  1. Blocos ```json com array de invokes: [{"name":"get_inventory",...}]
///  2. Marcação DSML nativa do modelo (DeepSeek):
///     <｜DSML｜tool_invokes><invoke name="...">...</invoke></｜DSML｜tool_invokes>
///  3. Blocos ```json com ação A2UI: {"version":"a2ui","action":"search",...}
///
/// Esses vazamentos aparecem principalmente quando o LLM responde sem usar
/// tools (promessas de ação) e no endpoint sync. A sanitização remove o que
/// for reconhecido como vazamento, preservando blocos json legítimos.
/// </summary>
public static partial class AiChatLeakSanitizer
{
    // Marcação DSML nativa do modelo. Formato real:
    // <｜DSML｜tool_invokes>...</｜DSML｜tool_invokes> — separador ｜ (U+FF5C
    // fullwidth) ou | ASCII, nome da seção em [a-z_]. Regex em partial class
    // para usar source generator (compilado) e evitar recompilação em runtime.
    [GeneratedRegex(@"<[/]?[｜|]DSML[｜|][a-z_]*>.*?<[/][｜|]DSML[｜|][a-z_]*>", RegexOptions.Singleline)]
    private static partial Regex DsmlBlockRegex();

    [GeneratedRegex(@"</?[｜|]DSML[｜|][a-z_]*>")]
    private static partial Regex DsmlOrphanRegex();

    [GeneratedRegex(@"<invoke\s+name=""[^""]*"">.*?</invoke>", RegexOptions.Singleline)]
    private static partial Regex InvokeTagRegex();

    [GeneratedRegex(@"<parameter\s+name=""[^""]*"">.*?</parameter>", RegexOptions.Singleline)]
    private static partial Regex ParameterTagRegex();

    [GeneratedRegex(@"</?[｜|]?tool_invokes[｜|]?>")]
    private static partial Regex ToolInvokesTagRegex();

    // Blocos de código ```json ... ``` (ou ``` ... ```) com conteúdo de vazamento.
    [GeneratedRegex("```(?:json)?\\s*([\\s\\S]*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonFenceRegex();

    [GeneratedRegex(@"^\s*\[\s*\{\s*""name""\s*:")]
    private static partial Regex InvokeArrayRegex();

    [GeneratedRegex(@"^\s*\{\s*""version""\s*:\s*""a2ui""")]
    private static partial Regex A2uiActionRegex();

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
        // 3. Remove <invoke>/<parameter>/<tool_invokes> soltos.
        foreach (var re in new[] { InvokeTagRegex(), ParameterTagRegex(), ToolInvokesTagRegex() })
        {
            if (re.IsMatch(clean))
            {
                clean = re.Replace(clean, string.Empty);
                removed = true;
            }
        }

        // 4. Remove blocos ```json cujo conteúdo seja um array de invokes ou
        // uma ação A2UI (o LLM "prometeu" executar tools como texto).
        clean = JsonFenceRegex().Replace(clean, m =>
        {
            var body = m.Groups[1].Value.Trim();
            if (InvokeArrayRegex().IsMatch(body) || A2uiActionRegex().IsMatch(body))
            {
                removed = true;
                return string.Empty;
            }
            return m.Value;
        });

        if (!removed)
            return (text, false);

        // Limpeza final: colapsa linhas vazias em excesso deixadas pelas remoções.
        clean = clean.Trim();
        clean = Regex.Replace(clean, @"\n{3,}", "\n\n");
        return (clean, true);
    }
}
