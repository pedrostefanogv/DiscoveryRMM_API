using Meduza.Core.Enums;

namespace Meduza.Core.ValueObjects;

/// <summary>
/// Metadados de um objeto armazenado no Object Storage.
/// Representa um arquivo/artefato genérico com isolamento por cliente/escopo.
/// </summary>
public class StorageObject
{
    /// <summary>Chave única do objeto no bucket (ex: clients/{clientId}/reports/{reportId}/{filename})</summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>Nome do bucket onde está armazenado</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Tipo MIME do arquivo</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Tamanho em bytes</summary>
    public long SizeBytes { get; set; }

    /// <summary>Checksum/ETag do arquivo para integridade</summary>
    public string? Checksum { get; set; }

    /// <summary>URL pública pré-assinada (se gerada), com expiração</summary>
    public string? PresignedUrl { get; set; }

    /// <summary>Tipo de provedor onde está armazenado</summary>
    public ObjectStorageProviderType StorageProvider { get; set; }

    /// <summary>Data/hora de quando foi armazenado</summary>
    public DateTime StoredAt { get; set; }

    /// <summary>Extrair nome do arquivo da ObjectKey</summary>
    public string GetFileName()
    {
        var parts = ObjectKey.Split('/');
        return parts.Length > 0 ? parts[^1] : ObjectKey;
    }

    /// <summary>Extrair extensão do arquivo</summary>
    public string GetFileExtension()
    {
        var fileName = GetFileName();
        var lastDot = fileName.LastIndexOf('.');
        return lastDot > 0 ? fileName.Substring(lastDot + 1) : string.Empty;
    }

    /// <summary>Validar se os metadados estão completos</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ObjectKey))
            errors.Add("ObjectKey é obrigatório");

        if (string.IsNullOrWhiteSpace(Bucket))
            errors.Add("Bucket é obrigatório");

        if (string.IsNullOrWhiteSpace(ContentType))
            errors.Add("ContentType é obrigatório");

        if (SizeBytes <= 0)
            errors.Add("SizeBytes deve ser maior que 0");

        return errors;
    }
}
