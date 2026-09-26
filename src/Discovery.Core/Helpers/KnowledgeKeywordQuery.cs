using System.Text;
using System.Text.RegularExpressions;

namespace Discovery.Core.Helpers;

/// <summary>
/// Tokenização de consultas de busca keyword da base de conhecimento.
/// Antes a query inteira virava um único padrão ILIKE ("%como configurar a vpn%"),
/// então perguntas em linguagem natural nunca encontravam os artigos. Agora a
/// query é quebrada em termos relevantes, procurados com OR.
/// </summary>
public static class KnowledgeKeywordQuery
{
    /// <summary>Teto de termos por busca (evita OR gigante e queries caras).</summary>
    public const int MaxTerms = 8;

    /// <summary>Comprimento mínimo de cada termo (descarta artigos, preposições curtas).</summary>
    public const int MinTermLength = 3;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "para", "por", "com", "como", "que", "uma", "das", "dos", "nos", "nas",
        "aos", "esta", "estao", "ser", "the", "and", "for", "sobre", "qual",
        "quais", "artigo", "artigos", "base", "conhecimento"
    };

    private static readonly Regex TokenRegex = new("[a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Normaliza (minúsculas, sem acentos), quebra em tokens alfanuméricos,
    /// descarta termos curtos e stopwords e limita a <see cref="MaxTerms"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var decomposed = query.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var normalized = sb.ToString().Normalize(NormalizationForm.FormC);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<string>(MaxTerms);
        foreach (Match match in TokenRegex.Matches(normalized))
        {
            var token = match.Value;
            if (token.Length < MinTermLength) continue;
            if (Stopwords.Contains(token)) continue;
            if (!seen.Add(token)) continue;
            terms.Add(token);
            if (terms.Count >= MaxTerms) break;
        }

        return terms;
    }
}
