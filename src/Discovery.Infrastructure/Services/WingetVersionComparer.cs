namespace Discovery.Infrastructure.Services;

/// <summary>
/// Comparador de versões tolerante para pacotes Winget (server-side).
/// Espelho do comparador do agent (automation_versions.go) para que servidor e
/// agent decidam igual. Winget não garante semver: lida com "2026.1.3",
/// "132.0.1", "1.29.289.0", sufixos "b12345"/"r2", pré-releases "beta"/"rc".
/// </summary>
public static class WingetVersionComparer
{
    /// <summary>
    /// Compara duas versões. Retorna &lt; 0 se a &lt; b, 0 se iguais, &gt; 0 se a &gt; b.
    /// null/vazio é sempre "menor" (não bloqueia import).
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 0;
        if (string.IsNullOrWhiteSpace(a)) return -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        var segsA = Tokenize(a);
        var segsB = Tokenize(b);
        var len = Math.Max(segsA.Count, segsB.Count);

        for (var i = 0; i < len; i++)
        {
            var ta = i < segsA.Count ? segsA[i] : Token.Zero;
            var tb = i < segsB.Count ? segsB[i] : Token.Zero;

            var cmp = ta.CompareTo(tb);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    /// <summary>Retorna true se <paramref name="candidate"/> é uma versão mais nova que <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>Comparer reutilizável para OrderBy/OrderByDescending (case-insensitive via Compare).</summary>
    public static readonly IComparer<string?> Default = Comparer<string?>.Create(Compare);

    private static List<Token> Tokenize(string version)
    {
        var tokens = new List<Token>();
        var span = version.Trim().TrimStart('v', 'V');
        var start = 0;

        for (var i = 0; i <= span.Length; i++)
        {
            var isBoundary = i == span.Length || !char.IsLetterOrDigit(span[i]);
            if (!isBoundary)
                continue;

            if (i > start)
                tokens.Add(ParseToken(span[start..i]));

            start = i + 1;
        }

        return tokens;
    }

    private static Token ParseToken(string raw)
    {
        // Segmento puramente numérico
        if (int.TryParse(raw, out var num))
            return new Token(num, null);

        // Segmento misto: extrai prefixo numérico ("36551b" → 36551 + "b")
        var digits = 0;
        while (digits < raw.Length && char.IsDigit(raw[digits]))
            digits++;

        if (digits > 0 && int.TryParse(raw[..digits], out var prefix))
            return new Token(prefix, raw[digits..].ToLowerInvariant());

        // Segmento textual: pré-release conhecido tem peso negativo (beta < estável)
        return new Token(0, raw.ToLowerInvariant());
    }

    private readonly record struct Token(int Number, string? Text) : IComparable<Token>
    {
        public static readonly Token Zero = new(0, null);

        public int CompareTo(Token other)
        {
            // Numérico vs numérico
            if (Text is null && other.Text is null)
                return Number.CompareTo(other.Number);

            // Pré-release textual pesa menos que segmento numérico puro ("beta" < "0")
            if (Text is not null && other.Text is null)
                return IsPreRelease(Text) ? -1 : 1;
            if (Text is null && other.Text is not null)
                return IsPreRelease(other.Text) ? 1 : -1;

            // Ambos textuais: pré-release conhecido < sufixo qualquer < estável
            var preA = Text is not null && IsPreRelease(Text);
            var preB = other.Text is not null && IsPreRelease(other.Text);
            if (preA != preB)
                return preA ? -1 : 1;

            return string.CompareOrdinal(Text, other.Text);
        }

        private static bool IsPreRelease(string text) =>
            text is "alpha" or "beta" or "rc" or "pre" or "preview" or "dev";
    }
}
