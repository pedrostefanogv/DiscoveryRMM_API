using System.Text.RegularExpressions;

namespace Discovery.Core.Helpers;

/// <summary>
/// Utilitários do token de imagem da base de conhecimento
/// (![alt](kbimage:&lt;attachmentId&gt;)).
///
/// O token é a referência ESTÁVEL persistida no Markdown: não é URL pública,
/// não expira e serve tanto ao console quanto ao agente. Tudo que entrega
/// conteúdo ao LLM (resumos, citações da busca keyword) ou indexa embeddings
/// precisa passar por aqui para não vazar o token nem poluir o texto.
/// </summary>
public static partial class KnowledgeImageMarkdown
{
    /// <summary>GUID no formato "D" (8-4-4-4-12), maiúsculas ou minúsculas.</summary>
    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    // Captura o GUID do token COMPLETO (![alt](kbimage:<id>)). O padrão não casa
    // uma menção textual a "kbimage:<guid>" fora da sintaxe de imagem — era o que
    // o regex antigo fazia (flag inUse marcada por engano em texto solto).
    [GeneratedRegex(@"!\[[^\]]*\]\(\s*kbimage:\s*(" + GuidPattern + @")\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    /// <summary>IDs referenciados no markdown, sem repetição e sem inválidos.</summary>
    public static IReadOnlyList<Guid> ExtractIds(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];

        var seen = new HashSet<Guid>();
        var ids = new List<Guid>();
        foreach (Match match in TokenRegex().Matches(markdown))
        {
            if (!Guid.TryParse(match.Groups[1].Value, out var id)) continue;
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Remove os tokens de imagem (o texto vira "sem aquele trecho"), já
    /// colapsando linhas em branco que sobraram. Usado no que vai ao LLM.
    /// </summary>
    public static string StripTokens(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? string.Empty;

        var stripped = TokenRegex().Replace(markdown, string.Empty);
        return BlankLinesRegex().Replace(stripped, "\n\n").Trim();
    }

    /// <summary>
    /// Remove APENAS o token do anexo informado (exclusão da imagem). Não
    /// normaliza o resto do documento: um Trim/colapso global reescreveria o
    /// artigo inteiro (inclusive linhas em branco dentro de code fences).
    /// Quando o token ocupa a linha sozinho, a linha inteira é removida.
    /// </summary>
    public static string RemoveToken(string? markdown, Guid attachmentId)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown ?? string.Empty;

        var token = @"!\[[^\]]*\]\(\s*kbimage:\s*" + attachmentId.ToString("D") + @"\s*\)";

        var aloneOnLine = Regex.Replace(
            markdown,
            @"^[ \t]*" + token + @"[ \t]*(?:\r?\n|$)",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Token no meio de uma linha com texto: remove só o token.
        return Regex.Replace(aloneOnLine, token, string.Empty, RegexOptions.IgnoreCase);
    }

    public static bool ContainsToken(string? markdown)
        => !string.IsNullOrWhiteSpace(markdown) && TokenRegex().IsMatch(markdown);
}
