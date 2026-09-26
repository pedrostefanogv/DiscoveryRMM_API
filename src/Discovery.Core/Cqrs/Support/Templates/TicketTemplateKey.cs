using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Discovery.Core.Cqrs.Support.Templates;

/// <summary>
/// Chave padronizada do template de chamado: identificador legível ([a-z0-9_])
/// único dentro do escopo (cliente + departamento, com NULLs contando como
/// iguais — então globais também são únicos entre si).
/// </summary>
public static class TicketTemplateKey
{
    public const int MaxLength = 80;

    private static readonly Regex ValidPattern = new("^[a-z0-9_]{2,80}$", RegexOptions.Compiled);
    private static readonly Regex NonSlugChars = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>Normaliza a chave (trim + minúsculas).</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValid(string? value) => ValidPattern.IsMatch(value ?? string.Empty);

    /// <summary>Gera a chave a partir de um texto humano (ex.: "Criação de Login" -> criacao_de_login).</summary>
    public static string Slugify(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(ch);
        }

        var slug = NonSlugChars.Replace(builder.ToString().ToLowerInvariant(), "_").Trim('_');
        return slug.Length <= MaxLength ? slug : slug[..MaxLength].Trim('_');
    }

    /// <summary>
    /// Resolve a chave a partir do valor recebido: aceita o formato padronizado e,
    /// por compatibilidade com clientes antigos, converte um texto humano em slug.
    /// </summary>
    public static string Resolve(string? raw, out string? error)
    {
        error = null;
        var normalized = Normalize(raw);
        if (IsValid(normalized)) return normalized;

        var slug = Slugify(raw);
        if (IsValid(slug)) return slug;

        error = "A chave do template deve ter de 2 a 80 caracteres usando apenas letras minúsculas, números e _ (ex.: criacao_de_login).";
        return string.Empty;
    }
}
