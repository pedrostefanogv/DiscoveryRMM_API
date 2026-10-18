using System.Text.RegularExpressions;

namespace Discovery.Core.Cqrs.Patches;

/// <summary>
/// Validação estrita das chaves canônicas de atualização aceitas pela API
/// (mesma regra do agente): <c>guid:&lt;uuid&gt;:&lt;revisão&gt;</c> ou <c>kb:KBxxxx</c>.
/// </summary>
public static class PatchUpdateKeyValidator
{
    private static readonly Regex GuidKeyRegex = new(
        "^guid:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:[0-9]{1,10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KbKeyRegex = new(
        "^kb:KB[0-9]{4,10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        var value = key.Trim();
        return GuidKeyRegex.IsMatch(value) || KbKeyRegex.IsMatch(value);
    }

    /// <summary>Normaliza a chave para minúsculas no GUID e KB em maiúsculas.</summary>
    public static string Normalize(string key)
    {
        var value = key.Trim();
        if (value.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            return value.ToLowerInvariant();
        if (value.StartsWith("kb:", StringComparison.OrdinalIgnoreCase))
            return "kb:" + value[3..].ToUpperInvariant();
        return value;
    }
}
