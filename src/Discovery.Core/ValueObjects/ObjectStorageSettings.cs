namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configuração de Object Storage para S3-compatível.
/// Encapsula validação e lógica de composição de prefixos.
/// </summary>
public class ObjectStorageSettings
{
    /// <summary>Nome do bucket global (ex: discovery-files, discovery-r2)</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Endpoint do provedor S3-compatível (ex: s3.amazonaws.com, api.cloudflare.com, object.oracle.com)</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Região (ex: us-east-1, auto para R2)</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Access Key ID</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key (criptografada em repouso no BD)</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>TTL padrão para URLs pré-assinadas (horas, default 24)</summary>
    public int UrlTtlHours { get; set; } = 24;

    /// <summary>Usar path-style URLs em vez de virtual-hosted style (necessário para alguns provedores S3-compat)</summary>
    public bool UsePathStyle { get; set; } = false;

    /// <summary>Verificar certificado SSL (false apenas para dev/self-signed)</summary>
    public bool SslVerify { get; set; } = true;

    /// <summary>Compor prefixo de bucket para isolamento por cliente e área</summary>
    /// <param name="clientId">GUID do cliente (null para dados globais)</param>
    /// <param name="area">Área funcional (ex: reports, tickets, notes, packages)</param>
    /// <returns>Prefixo no padrão: clients/{clientId}/{area}/ ou global/{area}/</returns>
    public string GetBucketPrefix(Guid? clientId, string area)
    {
        if (string.IsNullOrWhiteSpace(area))
            throw new ArgumentException("Area cannot be empty", nameof(area));

        if (clientId.HasValue && clientId != Guid.Empty)
            return $"clients/{clientId:N}/{area}/";

        return $"global/{area}/";
    }

    /// <summary>Validar se a configuração está completa e válida</summary>
    /// <returns>Lista de erros (vazio se válida)</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BucketName))
            errors.Add("BucketName é obrigatório");

        if (string.IsNullOrWhiteSpace(Endpoint))
            errors.Add("Endpoint é obrigatório");

        if (string.IsNullOrWhiteSpace(Region))
            errors.Add("Region é obrigatório");

        if (string.IsNullOrWhiteSpace(AccessKey))
            errors.Add("AccessKey é obrigatório");

        if (string.IsNullOrWhiteSpace(SecretKey))
            errors.Add("SecretKey é obrigatório");

        if (UrlTtlHours <= 0)
            errors.Add("UrlTtlHours deve ser maior que 0");

        return errors;
    }

    /// <summary>Verificar se a configuração está totalmente configurada (não está em modo Local vazio)</summary>
    public bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(BucketName) &&
               !string.IsNullOrWhiteSpace(Endpoint) &&
               !string.IsNullOrWhiteSpace(AccessKey) &&
               !string.IsNullOrWhiteSpace(SecretKey);
    }
}
