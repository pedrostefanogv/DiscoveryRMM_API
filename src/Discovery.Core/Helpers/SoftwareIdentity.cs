using System.Security.Cryptography;
using System.Text;

namespace Discovery.Core.Helpers;

/// <summary>
/// Identidade canônica de um software no catálogo (<c>software_catalog</c>).
/// </summary>
/// <remarks>
/// O fingerprint define qual linha de catálogo representa um produto e é usado
/// como chave de upsert por <c>IAgentSoftwareRepository.ReplaceInventoryAsync</c>.
///
/// Antes ele era composto por nome + publisher + install_id + serial + source.
/// Esses três últimos são voláteis: variam por máquina, por hive de registro
/// (32/64 bits), por coletor (registry × osquery × winget) e pelo próprio
/// UninstallString gravado pelo instalador. O resultado era uma linha de
/// catálogo POR INSTALAÇÃO — "Foxit PDF Editor" aparecia 2-3× na tela de
/// inventário, com a mesma versão, e o publisher virava "Sem fabricante" na
/// primeira coleta antes de o registro responder.
///
/// A identidade passou a ser apenas o NOME NORMALIZADO do produto (trim,
/// minúsculas e espaços internos colapsados). Publisher/source/instalador
/// continuam sendo gravados como metadados de exibição, mas não definem mais
/// a identidade.
/// </remarks>
public static class SoftwareIdentity
{
    /// <summary>Placeholder gravado por versões antigas em vez de publisher nulo.</summary>
    public const string UnknownPublisher = "Sem fabricante";

    /// <summary>
    /// Normaliza o nome de exibição usado como identidade: trim, minúsculas
    /// (invariante) e espaços internos colapsados em um único espaço.
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var previousWasSpace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            previousWasSpace = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fingerprint SHA-256 (hex maiúsculo) do nome normalizado — chave única
    /// de <c>software_catalog.fingerprint</c>.
    /// </summary>
    public static string BuildFingerprint(string? name)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeName(name))));

    /// <summary>Indica se o valor é um publisher real (não vazio nem placeholder).</summary>
    public static bool HasKnownPublisher(string? publisher)
        => !string.IsNullOrWhiteSpace(publisher)
           && !string.Equals(publisher.Trim(), UnknownPublisher, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converte o placeholder legado "Sem fabricante" em <c>null</c>, para que
    /// a UI use o fallback dela e para que o valor não "envenene" o catálogo.
    /// </summary>
    public static string? NormalizePublisher(string? publisher)
        => HasKnownPublisher(publisher) ? publisher!.Trim() : null;

    /// <summary>
    /// Compara duas versões de software por segmentos numéricos.
    /// Retorna &gt; 0 quando <paramref name="left"/> é mais nova.
    /// </summary>
    /// <remarks>
    /// Um produto pode aparecer duas vezes na coleta (hives 32/64 bits, registro +
    /// osquery) com versões diferentes. Como o catálogo guarda UMA versão por
    /// produto, ficar com "a primeira do payload" escolhia a versão conforme a
    /// ordem do coletor — no osquery a lista vem ordenada por nome+versão, ou
    /// seja a MAIS ANTIGA primeiro. Comparar por segmentos mantém a mais nova.
    /// Versões não numéricas caem para comparação ordinal.
    /// </remarks>
    public static int CompareVersions(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
            return 0;
        if (string.IsNullOrWhiteSpace(left))
            return -1;
        if (string.IsNullOrWhiteSpace(right))
            return 1;

        var a = TokenizeVersion(left);
        var b = TokenizeVersion(right);

        if (a.Count == 0 || b.Count == 0)
            return string.CompareOrdinal(left, right);

        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
                return x.CompareTo(y);
        }

        // Mesmos números (ex.: "14.0" × "14.0.0"): desempata pelo texto para
        // manter a escolha determinística.
        return string.CompareOrdinal(left, right);
    }

    /// <summary>Devolve a versão mais nova entre as duas, ignorando vazios.</summary>
    public static string? PreferNewestVersion(string? current, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(current))
            return incoming;
        if (string.IsNullOrWhiteSpace(incoming))
            return current;

        return CompareVersions(incoming, current) > 0 ? incoming : current;
    }

    private static List<long> TokenizeVersion(string value)
    {
        var numbers = new List<long>();
        long current = 0;
        var hasDigits = false;

        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                current = (current * 10) + (ch - '0');
                hasDigits = true;
                continue;
            }

            if (hasDigits)
            {
                numbers.Add(current);
                current = 0;
                hasDigits = false;
            }
        }

        if (hasDigits)
            numbers.Add(current);

        return numbers;
    }
}
