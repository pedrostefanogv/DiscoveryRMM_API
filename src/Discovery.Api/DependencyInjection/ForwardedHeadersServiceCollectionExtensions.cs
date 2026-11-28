using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Resolução do IP real do cliente atrás de proxies reversos (Cloudflare -> nginx -> API).
/// </summary>
public static class ForwardedHeadersServiceCollectionExtensions
{
    /// <summary>
    /// Default trusted proxies: ranges oficiais do Cloudflare (IPv4/IPv6) + loopback
    /// (nginx local em dev). Mantidos por padrão mesmo quando Security:TrustedProxies
    /// ou Security:TrustedProxyNetworks estão configurados.
    /// </summary>
    private static readonly string[] DefaultTrustedEntries =
    [
        // Cloudflare IPv4 ranges (https://www.cloudflare.com/ips/)
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        // Cloudflare IPv6 ranges
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32",
        "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
        // Localhost para nginx reverse proxy local
        "127.0.0.1", "::1"
    ];

    /// <summary>
    /// Registra as opções de ForwardedHeaders usadas pelo middleware de proxy.
    /// </summary>
    public static IServiceCollection AddDiscoveryForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
            ApplyDiscoveryForwardedHeaders(options, configuration));
        return services;
    }

    /// <summary>
    /// Aplica a política de proxies confiáveis. Exposto para permitir teste unitário
    /// do comportamento real do middleware sem subir a aplicação.
    /// </summary>
    public static void ApplyDiscoveryForwardedHeaders(
        ForwardedHeadersOptions options,
        IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var configuredProxies = configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? [];
        var configuredNetworks = configuration.GetSection("Security:TrustedProxyNetworks").Get<string[]>() ?? [];

        // DECISÃO (merge, não replace): antes as chaves de configuração SUBSTITUÍAM os
        // defaults do Cloudflare/loopback. Um deploy que definisse Security:TrustedProxies
        // (ex.: apenas o IP do nginx) perdia todos os ranges do Cloudflare e o XFF
        // voltava a não ser resolvido — exatamente o bug do ClientIp = 127.0.0.1.
        // Agora os defaults são sempre preservados e a configuração apenas ADICIONA
        // proxies confiáveis. Continua sendo uma allow-list fechada: nada é confiado
        // por default além do Cloudflare e do loopback.
        var allEntries = DefaultTrustedEntries
            .Concat(configuredProxies)
            .Concat(configuredNetworks)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // DECISÃO (ForwardLimit = null): o default do ASP.NET Core é ForwardLimit = 1,
        // que faz o middleware examinar APENAS a última entrada do X-Forwarded-For.
        // O nginx monta o header com proxy_add_x_forwarded_for, então o XFF chega como
        // "<cliente>, <edge-do-cloudflare>"; examinando só a última entrada (o edge, que
        // É proxy conhecido) o middleware atinge o limite sem resolver cliente nenhum e
        // Connection.RemoteIpAddress permanece 127.0.0.1 (loopback do nginx).
        // Sem limite, o middleware percorre o header inteiro da direita para a esquerda
        // e para no primeiro IP NÃO confiável — que é justamente o IP do cliente anexado
        // pelo último proxy confiável. Entradas forjadas mais à esquerda (XFF enviado
        // pelo próprio cliente e preservado por Cloudflare/nginx) nunca são alcançadas.
        // XFF forjado por quem fala direto com a origem também não vale: se o peer direto
        // não está na allow-list o middleware ignora o header por completo.
        options.ForwardLimit = null;

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in allEntries)
        {
            var parts = entry.Split('/');
            if (parts.Length == 2)
            {
                if (System.Net.IPAddress.TryParse(parts[0], out var netAddr)
                    && int.TryParse(parts[1], out var prefix)
                    && prefix >= 0
                    && prefix <= (netAddr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32))
                {
                    // System.Net.IPNetwork (não o tipo legado de HttpOverrides).
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(netAddr, prefix));
                }
                continue;
            }

            if (System.Net.IPAddress.TryParse(entry, out var parsed))
                options.KnownProxies.Add(parsed);
        }
    }
}
