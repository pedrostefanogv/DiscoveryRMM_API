using System.Text;

namespace Discovery.Core.Helpers;

/// <summary>
/// Validação e sanitização centralizada de nomes de arquivo, caminhos virtuais,
/// extensões e tipos MIME para uploads (Central de Arquivos e Links de Upload).
/// </summary>
public static class FileValidationHelper
{
    private const int MaxFileNameLength = 200;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Sanitiza um nome de arquivo: remove diretórios, caracteres de controle/inválidos,
    /// evita nomes reservados do Windows e limita o comprimento preservando a extensão.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var raw = (fileName ?? string.Empty).Replace('\\', '/').Trim();
        var lastSlash = raw.LastIndexOf('/');
        if (lastSlash >= 0)
            raw = raw[(lastSlash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || invalid.Contains(ch) || ch == '/' || ch == '\\')
                sb.Append('_');
            else
                sb.Append(ch);
        }

        var clean = sb.ToString().Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(clean))
            clean = "arquivo";

        var ext = Path.GetExtension(clean);
        var stem = Path.GetFileNameWithoutExtension(clean);
        if (WindowsReservedNames.Contains(stem))
            stem = "_" + stem;

        clean = stem + ext;
        if (clean.Length > MaxFileNameLength)
        {
            var maxStem = Math.Max(1, MaxFileNameLength - ext.Length);
            clean = stem[..Math.Min(stem.Length, maxStem)] + ext;
        }

        return clean;
    }

    /// <summary>
    /// Normaliza um caminho virtual de pasta (<c>/docs/drivers</c>), bloqueando
    /// traversal (<c>..</c>) e barras invertidas.
    /// </summary>
    public static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return "/";

        var segments = folderPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s is not "." and not "..")
            .ToArray();

        return segments.Length == 0 ? "/" : "/" + string.Join("/", segments);
    }

    /// <summary>
    /// Sanitiza e valida o nome de uma pasta individual (sem barras ou segmentos de traversal).
    /// </summary>
    public static string SanitizeFolderName(string? name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clean))
            throw new InvalidOperationException("O nome da pasta é obrigatório.");
        if (clean is "." or "..")
            throw new InvalidOperationException("Nome de pasta inválido.");
        if (clean.Contains('/') || clean.Contains('\\'))
            throw new InvalidOperationException("O nome da pasta não pode conter '/' ou '\\'.");
        if (clean.Any(char.IsControl))
            throw new InvalidOperationException("O nome da pasta contém caracteres inválidos.");
        if (clean.Length > 120)
            clean = clean[..120].Trim();
        if (string.IsNullOrWhiteSpace(clean))
            throw new InvalidOperationException("O nome da pasta é obrigatório.");

        return clean;
    }

    /// <summary>
    /// Retorna o caminho da pasta pai de um <paramref name="folderPath"/> normalizado.
    /// </summary>
    public static string GetParentFolderPath(string? folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (normalized == "/")
            return "/";

        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : normalized[..lastSlash];
    }

    /// <summary>
    /// Combina o caminho pai normalizado com um nome de pasta validado.
    /// </summary>
    public static string CombineFolderPath(string? parentPath, string folderName)
    {
        var parent = NormalizeFolderPath(parentPath);
        var safeName = SanitizeFolderName(folderName);
        return parent == "/" ? "/" + safeName : parent + "/" + safeName;
    }

    /// <summary>
    /// Normaliza uma lista de extensões (ex.: <c>pdf</c> → <c>.pdf</c>).
    /// </summary>
    public static string[] NormalizeExtensions(IEnumerable<string>? extensions)
    {
        if (extensions is null)
            return [];

        return extensions
            .Select(e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Normaliza uma lista de tipos MIME (ex.: <c>application/pdf</c>, <c>image/*</c>).
    /// </summary>
    public static string[] NormalizeMimeTypes(IEnumerable<string>? mimeTypes)
    {
        if (mimeTypes is null)
            return [];

        return mimeTypes
            .Select(m => (m ?? string.Empty).Trim().ToLowerInvariant())
            .Where(m => !string.IsNullOrWhiteSpace(m) && m.Contains('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Verifica se o arquivo atende às restrições de extensão e MIME configuradas.
    /// Listas vazias significam "sem restrição".
    /// </summary>
    public static (bool Allowed, string? Reason) ValidateFileType(
        string fileName,
        string? contentType,
        IReadOnlyCollection<string>? allowedExtensions,
        IReadOnlyCollection<string>? allowedMimeTypes)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (allowedExtensions is { Count: > 0 })
        {
            if (string.IsNullOrEmpty(ext) || !allowedExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"Extensão '{(string.IsNullOrEmpty(ext) ? "(sem extensão)" : ext)}' não permitida. Permitidas: {string.Join(", ", allowedExtensions)}.");
            }
        }

        if (allowedMimeTypes is { Count: > 0 })
        {
            var normalizedMime = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(normalizedMime))
                normalizedMime = "application/octet-stream";

            var matched = allowedMimeTypes.Any(pattern =>
            {
                var p = pattern.Trim().ToLowerInvariant();
                if (p == "*/*")
                    return true;
                if (p.EndsWith("/*", StringComparison.Ordinal))
                {
                    var prefix = p[..^1]; // "image/"
                    return normalizedMime.StartsWith(prefix, StringComparison.Ordinal);
                }
                return string.Equals(p, normalizedMime, StringComparison.Ordinal);
            });

            if (!matched)
            {
                return (false, $"Tipo MIME '{normalizedMime}' não permitido. Permitidos: {string.Join(", ", allowedMimeTypes)}.");
            }
        }

        return (true, null);
    }
}
