namespace Discovery.Core.Configuration;

/// <summary>
/// Object storage gerenciado pelo proprio servidor (RustFS local).
///
/// Quando Mode=internal, o armazenamento NAO vem do banco: as credenciais e os
/// endpoints vem do env do projeto (discovery.env -> ObjectStorage__*). O
/// servidor fala com o storage pelo ENDPOINT INTERNO (loopback) e as URLs
/// pre-assinadas entregues aos agentes usam o ENDPOINT PUBLICO do servidor
/// (ex.: https://portal.suaempresa.com.br), que e o host que o nginx publica.
/// </summary>
public class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    /// <summary>database (padrao, configuracao pelo console) | internal (RustFS do servidor).</summary>
    public string Mode { get; set; } = "database";

    /// <summary>Endpoint usado pelo SERVIDOR (loopback), ex.: 127.0.0.1:9000.</summary>
    public string InternalEndpoint { get; set; } = string.Empty;

    /// <summary>Endpoint publico usado nas URLs pre-assinadas, ex.: https://portal.suaempresa.com.br.</summary>
    public string PublicEndpoint { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UsePathStyle { get; set; } = true;
    public bool SslVerify { get; set; } = true;
    public int UrlTtlHours { get; set; } = 24;

    /// <summary>Modo interno ligado (RustFS do servidor, chaves do env).</summary>
    public bool IsInternal =>
        string.Equals(Mode?.Trim(), "internal", StringComparison.OrdinalIgnoreCase);
}
