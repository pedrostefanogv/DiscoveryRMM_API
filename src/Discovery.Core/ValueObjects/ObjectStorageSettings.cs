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
        else if (BucketName.Contains('/') || BucketName.Contains('\\') || BucketName.Contains(' '))
            errors.Add("BucketName deve ser um nome de bucket válido sem barras ou espaços");

        if (string.IsNullOrWhiteSpace(Endpoint))
            errors.Add("Endpoint é obrigatório");
        else if (!TryValidateEndpoint(Endpoint))
            errors.Add("Endpoint deve ser um host válido ou URL http/https sem path, query ou fragment");

        if (string.IsNullOrWhiteSpace(Region))
            errors.Add("Region é obrigatório");
        else if (Region.Contains(' '))
            errors.Add("Region não pode conter espaços");

        if (string.IsNullOrWhiteSpace(AccessKey))
            errors.Add("AccessKey é obrigatório");

        if (string.IsNullOrWhiteSpace(SecretKey))
            errors.Add("SecretKey é obrigatório");

        if (UrlTtlHours is < 1 or > 168)
            errors.Add("UrlTtlHours deve estar entre 1 e 168 horas");

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

    private static bool TryValidateEndpoint(string rawEndpoint)
    {
        var trimmed = rawEndpoint.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return (absoluteUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || absoluteUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                   && !string.IsNullOrWhiteSpace(absoluteUri.Host)
                   && string.IsNullOrEmpty(absoluteUri.AbsolutePath.Trim('/'))
                   && string.IsNullOrEmpty(absoluteUri.Query)
                   && string.IsNullOrEmpty(absoluteUri.Fragment)
                   && string.IsNullOrEmpty(absoluteUri.UserInfo);
        }

        return Uri.CheckHostName(trimmed.Split(':')[0]) != UriHostNameType.Unknown;
    }
}
