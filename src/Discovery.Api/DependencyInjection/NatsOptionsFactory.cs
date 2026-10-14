using NATS.Client.Core;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Monta as opcoes de conexao do NATS (URL, credenciais e TLS) a partir da
/// configuracao.
///
/// Existe para que a conexao PRINCIPAL e o HEALTH CHECK usem exatamente as mesmas
/// credenciais: o health check montava NatsOpts sem AuthOpts, conectava anonimo
/// (User "") e, com o auth_callout habilitado no broker, recebia
/// "Authorization Violation" — o /health respondia 503 e o instalador abortava o
/// bootstrap administrativo mesmo com a API saudavel.
/// </summary>
internal static class NatsOptionsFactory
{
    public static NatsOpts CreateBase(IConfiguration configuration)
    {
        var natsUrl = configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
        var natsAuthUser = configuration.GetValue<string>("Nats:AuthUser");
        var natsAuthPassword = configuration.GetValue<string>("Nats:AuthPassword");

        var opts = new NatsOpts { Url = natsUrl };

        if (!string.IsNullOrWhiteSpace(natsAuthUser) && !string.IsNullOrWhiteSpace(natsAuthPassword))
        {
            opts = opts with
            {
                AuthOpts = new NatsAuthOpts { Username = natsAuthUser, Password = natsAuthPassword }
            };
        }

        // TLS: quando a URL usa prefixo tls:// ou wss://, ativa TLS automaticamente.
        if (natsUrl.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
            || natsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            opts = opts with { TlsOpts = new NatsTlsOpts() };
        }

        return opts;
    }
}
