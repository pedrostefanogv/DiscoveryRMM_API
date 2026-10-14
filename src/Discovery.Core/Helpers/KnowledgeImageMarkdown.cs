using System.Globalization;
using System.Text.RegularExpressions;

namespace Discovery.Core.Helpers;

/// <summary>
/// Utilitários do token de imagem da base de conhecimento
/// (![alt](kbimage:&lt;attachmentId&gt; "w=50%")).
///
/// O token é a referência ESTÁVEL persistida no Markdown: não é URL pública, não
/// expira e serve tanto ao console quanto ao agente. Tudo que entrega conteúdo ao
/// LLM (resumos, citações da busca keyword) ou indexa embeddings precisa passar
/// por aqui para não vazar o token nem poluir o texto.
///
/// O tamanho é opcional e vai no TÍTULO do Markdown ("w=50%" ou "w=420"): o
/// Markdown não tem atributo de largura e HTML cru não é renderizado pelo
/// react-markdown sem rehype-raw (funcionaria no preview do editor e sumiria na
/// leitura — pior que não ter).
/// </summary>
public static partial class KnowledgeImageMarkdown
{
    /// <summary>GUID no formato "D" (8-4-4-4-12), maiúsculas ou minúsculas.</summary>
    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    // Captura o GUID do token COMPLETO (![alt](kbimage:<id> ["w=..."])) e aceita
    // QUALQUER sufixo antes do ")" — inclusive título malformado escrito à mão
    // (ex.: "[...](kbimage:<id> "w=50%" extra)"). Sem essa tolerância o token
    // escapava do StripTokens (vazando para o LLM) e do ExtractIds (inUse errado).
    // Não casa uma menção textual a "kbimage:<guid>" fora da sintaxe de imagem.
    [GeneratedRegex(@"!\[[^\]]*\]\(\s*kbimage:\s*(" + GuidPattern + @")[^)\r\n]*\)", RegexOptions.IgnoreCase)]
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
    /// Normaliza o tamanho aceito no título do token: "w=50%" (1–100) ou
    /// "w=420" (1–4000 px). Devolve null quando vazio/fora do padrão — nesse caso
    /// o renderizador usa a largura natural limitada a 100% do container.
    /// </summary>
    public static string? NormalizeSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim();
        if (value.StartsWith("w=", StringComparison.OrdinalIgnoreCase))
            value = value[2..].Trim();

        var isPercent = value.EndsWith('%');
        var digits = isPercent ? value[..^1] : value;

        if (digits.Length is 0 or > 4) return null;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return null;
        }

        var number = int.Parse(digits, CultureInfo.InvariantCulture);
        if (isPercent)
            return number is >= 1 and <= 100 ? $"{number}%" : null;

        return number is >= 1 and <= 4000 ? number.ToString(CultureInfo.InvariantCulture) : null;
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

        var token = @"!\[[^\]]*\]\(\s*kbimage:\s*" + attachmentId.ToString("D") + @"[^)\r\n]*\)";

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
