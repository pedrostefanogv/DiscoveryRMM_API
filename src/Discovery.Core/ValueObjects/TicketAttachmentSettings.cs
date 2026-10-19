using System.Text.Json;
using Discovery.Core.Entities;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configuração de anexos do servidor, unificada para TODOS os contextos:
/// anexos de chamado (repositório), anexos de comentário, chat do chamado,
/// chat de suporte com o agent e chat de IA/LLM.
///
/// Dois limites por arquivo, de propósito:
///  - <see cref="MaxFileSizeBytes"/>: conversas (comentários, chats e IA). O
///    arquivo faz parte do fluxo da conversa e é exibido/baixado com frequência.
///  - <see cref="MaxLargeFileSizeBytes"/>: repositório de arquivos grandes do
///    chamado (aba Anexos, upload direto ao storage). Não é carregado na conversa.
///
/// A allowlist (<see cref="AllowedContentTypes"/>) é única e vale para os dois
/// níveis. O teto de 10 GiB acompanha o nginx (client_max_body_size 10240m).
/// </summary>
public class TicketAttachmentSettings
{
    public const long DefaultMaxFileSizeBytes = 10L * 1024 * 1024;          // 10 MB
    public const long DefaultMaxLargeFileSizeBytes = 2L * 1024 * 1024 * 1024; // 2 GB
    public const long HardUpperLimitBytes = 10L * 1024 * 1024 * 1024;       // 10 GiB

    /// <summary>Allowlist padrão: imagens, documentos/Office, textos e compactados.</summary>
    public static readonly string[] DefaultAllowedContentTypes =
    [
        // Imagens
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "image/bmp",
        "image/tiff",
        "image/svg+xml",
        "image/heic",
        "image/avif",
        // Office e documentos
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
        "application/rtf",
        "text/plain",
        "text/csv",
        "text/markdown",
        "application/json",
        "application/xml",
        // Compactados
        "application/zip",
        "application/x-7z-compressed",
        "application/x-rar-compressed",
        "application/x-tar",
        "application/gzip",
        "application/x-bzip2",
        "application/x-xz",
    ];

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Limite por arquivo nos contextos de conversa: anexo de comentário, chat
    /// do chamado, chat de suporte com o agent e chat de IA/LLM. Padrão: 10 MB.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>
    /// Limite por arquivo no repositório de anexos grandes do chamado (aba
    /// Anexos). Padrão: 2 GB. Precisa ser &gt;= <see cref="MaxFileSizeBytes"/>.
    /// </summary>
    public long MaxLargeFileSizeBytes { get; set; } = DefaultMaxLargeFileSizeBytes;

    public string[] AllowedContentTypes { get; set; } = [.. DefaultAllowedContentTypes];

    public int PresignedUploadUrlTtlMinutes { get; set; } = 15;

    public static TicketAttachmentSettings FromJson(string? json)
    {
        var parsed = TryParse(json);
        if (parsed is null)
            return new TicketAttachmentSettings();

        return Create(
            parsed,
            maxFileSizeFallback: null,
            maxLargeFileSizeFallback: null,
            allowedContentTypesFallback: null);
    }

    /// <summary>
    /// Configuração efetiva do servidor. Mantém compatibilidade com versões
    /// anteriores: quando o JSON não tem uma propriedade, usa as colunas legadas
    /// (AttachmentComment* / AttachmentTicket*) — que continuam sendo
    /// sincronizadas no PUT — e, por fim, os defaults.
    /// </summary>
    public static TicketAttachmentSettings FromServerConfiguration(ServerConfiguration server)
    {
        var parsed = TryParse(server.TicketAttachmentSettingsJson);

        // Preferência pela allowlist de COMENTÁRIO: é a mais restrita e era a
        // usada por comentários/chats antes da unificação. A lista do
        // repositório (mais ampla, inclui compactados/executáveis) só é usada
        // quando a de comentário está vazia — evita ampliar silenciosamente a
        // allowlist de conversas em bases que nunca salvaram o JSON pelo card.
        var legacyAllowedTypes = (server.AttachmentCommentAllowedContentTypes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (legacyAllowedTypes.Length == 0)
            legacyAllowedTypes = (server.AttachmentTicketAllowedContentTypes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Create(
            parsed,
            maxFileSizeFallback: server.AttachmentCommentMaxFileSizeBytes > 0
                ? server.AttachmentCommentMaxFileSizeBytes
                : null,
            maxLargeFileSizeFallback: server.AttachmentTicketMaxFileSizeBytes > 0
                ? server.AttachmentTicketMaxFileSizeBytes
                : null,
            allowedContentTypesFallback: legacyAllowedTypes.Length > 0 ? legacyAllowedTypes : null);
    }

    public string ToJson()
    {
        Normalize();
        return JsonSerializer.Serialize(this, JsonSerializerOptions.Web);
    }

    public string[] Validate()
    {
        var errors = new List<string>();

        if (MaxFileSizeBytes <= 0)
            errors.Add("MaxFileSizeBytes deve ser maior que zero.");
        else if (MaxFileSizeBytes > HardUpperLimitBytes)
            errors.Add("MaxFileSizeBytes não pode ultrapassar 10GB.");

        if (MaxLargeFileSizeBytes <= 0)
            errors.Add("MaxLargeFileSizeBytes deve ser maior que zero.");
        else if (MaxLargeFileSizeBytes > HardUpperLimitBytes)
            errors.Add("MaxLargeFileSizeBytes não pode ultrapassar 10GB.");
        else if (MaxLargeFileSizeBytes < MaxFileSizeBytes)
            errors.Add("MaxLargeFileSizeBytes deve ser maior ou igual a MaxFileSizeBytes.");

        if (PresignedUploadUrlTtlMinutes is < 1 or > 120)
            errors.Add("PresignedUploadUrlTtlMinutes deve estar entre 1 e 120.");

        var contentTypes = AllowedContentTypes ?? [];
        if (contentTypes.Length == 0)
            errors.Add("AllowedContentTypes deve conter ao menos um tipo MIME permitido.");

        if (contentTypes.Any(value => string.IsNullOrWhiteSpace(value)))
            errors.Add("AllowedContentTypes contém valor vazio/inválido.");

        return errors.ToArray();
    }

    public bool IsContentTypeAllowed(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        return (AllowedContentTypes ?? []).Contains(contentType.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Allowlist efetiva para o chat de IA: somente tipos image/*.</summary>
    public string[] GetAllowedImageContentTypes()
        => (AllowedContentTypes ?? [])
            .Where(value => value.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static TicketAttachmentSettings Create(
        Dto? parsed,
        long? maxFileSizeFallback,
        long? maxLargeFileSizeFallback,
        string[]? allowedContentTypesFallback)
    {
        var settings = new TicketAttachmentSettings
        {
            Enabled = parsed?.Enabled ?? true,
            MaxFileSizeBytes = PositiveOr(parsed?.MaxFileSizeBytes, maxFileSizeFallback, DefaultMaxFileSizeBytes),
            MaxLargeFileSizeBytes = PositiveOr(parsed?.MaxLargeFileSizeBytes, maxLargeFileSizeFallback, DefaultMaxLargeFileSizeBytes),
            // Lista vazia explícita é tratada como ausente: evita uma
            // configuração efetiva inutilizável (nenhum tipo permitido).
            AllowedContentTypes = parsed?.AllowedContentTypes is { Length: > 0 } parsedTypes
                ? parsedTypes
                : allowedContentTypesFallback ?? [.. DefaultAllowedContentTypes],
            PresignedUploadUrlTtlMinutes = parsed?.PresignedUploadUrlTtlMinutes ?? 15
        };

        settings.Normalize();
        return settings;
    }

    /// <summary>Valor positivo, senão fallback positivo, senão o default.</summary>
    private static long PositiveOr(long? value, long? fallback, long defaultValue)
        => value is > 0 ? value.Value
            : fallback is > 0 ? fallback.Value
            : defaultValue;

    private static Dto? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dto>(json, JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private void Normalize()
    {
        AllowedContentTypes = (AllowedContentTypes ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Um JSON antigo pode ter só o limite pequeno (inclusive valores de
        // repositório, como 2 GB). Nunca deixar o limite grande abaixo do
        // pequeno: sem isso o anexo do chamado ficaria menor que o de conversa.
        if (MaxLargeFileSizeBytes > 0 && MaxLargeFileSizeBytes < MaxFileSizeBytes)
            MaxLargeFileSizeBytes = MaxFileSizeBytes;
    }

    /// <summary>DTO nullable: distingue "propriedade ausente" de "valor default".</summary>
    private sealed class Dto
    {
        public bool? Enabled { get; set; }
        public long? MaxFileSizeBytes { get; set; }
        public long? MaxLargeFileSizeBytes { get; set; }
        public string[]? AllowedContentTypes { get; set; }
        public int? PresignedUploadUrlTtlMinutes { get; set; }
    }
}
