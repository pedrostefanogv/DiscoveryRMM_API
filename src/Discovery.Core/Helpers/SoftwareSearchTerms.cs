namespace Discovery.Core.Helpers;

/// <summary>
/// Converte o texto digitado na busca do inventário de software em termos
/// independentes de LIKE/ILIKE.
/// </summary>
/// <remarks>
/// A busca era um único padrão <c>%texto%</c>: procurar "Foxit Editor" não
/// encontrava "Foxit PDF Editor", porque a frase inteira não é substring do
/// nome. Agora o texto é quebrado em tokens e TODOS precisam casar (AND) —
/// "Foxit Editor" casa com "Foxit PDF Editor", "Adobe Acrobat Reader" casa com
/// "Adobe Acrobat Reader DC", e assim por diante. Cada token pode casar em
/// campos diferentes (nome, fabricante, versão, hostname...).
/// </remarks>
public static class SoftwareSearchTerms
{
    /// <summary>Caractere de escape usado nos padrões LIKE/ILIKE.</summary>
    public const string EscapeCharacter = "\\";

    /// <summary>
    /// Teto de termos considerados por busca. Protege a consulta SQL de entradas
    /// patológicas (uma cláusula OR por termo) sem afetar buscas reais.
    /// </summary>
    public const int MaxTerms = 8;

    /// <summary>
    /// Quebra a busca em termos distintos (case-insensitive), ignorando espaços
    /// repetidos. Retorna lista vazia quando o texto é vazio/composto só de espaços.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return [];

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Length == 0 || !seen.Add(term))
                continue;

            terms.Add(term);

            if (terms.Count >= MaxTerms)
                break;
        }

        return terms;
    }

    /// <summary>Monta o padrão ILIKE (<c>%termo%</c>) escapando curingas do usuário.</summary>
    public static string ToILikePattern(string term)
        => $"%{EscapeLikeWildcards(term)}%";

    /// <summary>
    /// Escapa <c>%</c>, <c>_</c> e o próprio caractere de escape para que sejam
    /// tratados como literais (ex.: buscar "100%" não deve casar com tudo).
    /// </summary>
    public static string EscapeLikeWildcards(string term)
    {
        if (string.IsNullOrEmpty(term))
            return string.Empty;

        var builder = new System.Text.StringBuilder(term.Length + 4);

        foreach (var ch in term)
        {
            if (ch is '%' or '_' or '\\')
                builder.Append('\\');

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>Padrões ILIKE prontos para uso; <c>null</c> quando não há busca.</summary>
    public static IReadOnlyList<string>? BuildPatterns(string? search)
    {
        var terms = Parse(search);
        if (terms.Count == 0)
            return null;

        return terms.Select(ToILikePattern).ToArray();
    }
}
