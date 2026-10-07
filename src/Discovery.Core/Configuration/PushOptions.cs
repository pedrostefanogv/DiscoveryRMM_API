namespace Discovery.Core.Configuration;

/// <summary>
/// Configuracao de Web Push (notificacoes do navegador).
/// As chaves VAPID identificam o servidor perante os provedores de push e devem
/// ser estaveis: regenera-las invalida todas as inscricoes existentes.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Liga/desliga o envio de Web Push (o restante do fluxo continua normal).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Chave publica VAPID (base64url). Exposta ao navegador.</summary>
    public string VapidPublicKey { get; set; } = string.Empty;

    /// <summary>Chave privada VAPID (base64url). Deve vir de secret/variravel de ambiente.</summary>
    public string VapidPrivateKey { get; set; } = string.Empty;

    /// <summary>Contato do servidor exigido pelos provedores (mailto: ou https:).</summary>
    public string VapidSubject { get; set; } = "mailto:suporte@discovery.local";

    /// <summary>Teto de inscricoes por usuario (dispositivos simultaneos).</summary>
    public int MaxSubscriptionsPerUser { get; set; } = 20;

    /// <summary>TTL do push em segundos — quanto tempo o provedor guarda a mensagem.</summary>
    public int TtlSeconds { get; set; } = 3600;

    /// <summary>
    /// Timeout (s) do HttpClient usado contra os provedores de push. O default do
    /// HttpClient e 100s — alto demais para um envio best-effort.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 10;

    /// <summary>Timeout (s) do lote inteiro de inscricoes de um usuario.</summary>
    public int BatchTimeoutSeconds { get; set; } = 15;

    /// <summary>Em Development, gera um par VAPID transitorio quando as chaves nao foram configuradas.</summary>
    public bool AllowDevelopmentKeyGeneration { get; set; } = true;
}
