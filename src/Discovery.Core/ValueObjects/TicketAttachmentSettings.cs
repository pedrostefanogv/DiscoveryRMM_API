using System.Text.Json;

namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configuração de anexos para Tickets.
/// Controla habilitação, tipos permitidos e tamanho máximo de upload.
/// </summary>
public class TicketAttachmentSettings
{
    public bool Enabled { get; set; } = true;

    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    public string[] AllowedContentTypes { get; set; } =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
        "application/pdf"
    ];

    public int PresignedUploadUrlTtlMinutes { get; set; } = 15;

    public static TicketAttachmentSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TicketAttachmentSettings();

        try
        {
            var parsed = JsonSerializer.Deserialize<TicketAttachmentSettings>(json, JsonSerializerOptions.Web);
            if (parsed is null)
                return new TicketAttachmentSettings();

            parsed.Normalize();
            return parsed;
        }
        catch
        {
            return new TicketAttachmentSettings();
        }
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

        // Teto de 10 GiB: mesmo limite do nginx (client_max_body_size 10240m) e
        // do upload multipart com retomada. Sem esse alinhamento o proxy
        // rejeitaria a requisicao (413) antes de a API validar o anexo.
        const long hardUpperLimitBytes = 10L * 1024 * 1024 * 1024;
        if (MaxFileSizeBytes > hardUpperLimitBytes)
            errors.Add("MaxFileSizeBytes não pode ultrapassar 10GB.");

        if (PresignedUploadUrlTtlMinutes is < 1 or > 120)
            errors.Add("PresignedUploadUrlTtlMinutes deve estar entre 1 e 120.");

        if (AllowedContentTypes.Length == 0)
            errors.Add("AllowedContentTypes deve conter ao menos um tipo MIME permitido.");

        if (AllowedContentTypes.Any(value => string.IsNullOrWhiteSpace(value)))
            errors.Add("AllowedContentTypes contém valor vazio/inválido.");

        return errors.ToArray();
    }

    public bool IsContentTypeAllowed(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        return AllowedContentTypes.Contains(contentType.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private void Normalize()
    {
        AllowedContentTypes = (AllowedContentTypes ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
