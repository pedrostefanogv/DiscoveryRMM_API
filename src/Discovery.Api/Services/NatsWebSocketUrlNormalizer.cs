using System;

namespace Discovery.Api.Services;

/// <summary>
/// Normaliza a URL externa do WebSocket do NATS para uso por viewers no browser.
///
/// BUG 2026-09-17 (acesso remoto: viewer nao conecta, code 1006): o valor
/// configurado em NatsWebSocketExternalUrl era "wss://tngplacas.com.br/nats"
/// (sem a barra final). O nginx faz "location = /nats { return 308 /nats/; }" —
/// mas navegadores NAO seguem redirects HTTP no handshake de WebSocket
/// (Firefox/Chrome falham com CloseEvent 1006 imediato). Os agentes Go nao
/// sofrem disso porque o nats.go envia o path via nats.ProxyPath (nunca na
/// URL); o viewer do browser e o unico cliente que depende do path na URL.
///
/// Normalizacao: garante barra final no path da URL (ex.: ".../nats" ->
/// ".../nats/"). Sem tocar em query string nem em URLs sem path.
/// </summary>
public static class NatsWebSocketUrlNormalizer
{
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed;

        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path.EndsWith("/"))
            return trimmed;

        // ".../nats" -> ".../nats/" preservando query/fragment.
        return trimmed.Replace(path, path + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve a URL WSS externa do NATS para os viewers (tela/terminal/arquivos/
    /// processos e console de debug).
    ///
    /// Ordem: 1) valor configurado em NatsWebSocketExternalUrl; 2) DERIVADO do
    /// NatsServerHostExternal quando NatsUseWssExternal estiver ligado
    /// ("wss://<host>/nats/").
    ///
    /// Por que derivar: em instalações novas o operador preenche o host externo
    /// (Nats__ServerHostExternal, escrito pelo instalador), mas o campo da URL
    /// WSS ficava vazio — a API devolvia natsWsUrl = null e o viewer do portal
    /// não abria o WebSocket ("Conectando ao agent... aguardando NATS"). O mesmo
    /// nginx que serve o portal publica o NATS em /nats/ no mesmo host, então a
    /// derivação é sempre válida.
    /// </summary>
    public static string? Resolve(string? configuredUrl, string? externalHost, bool useWssExternal)
    {
        var normalized = Normalize(configuredUrl);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var host = (externalHost ?? string.Empty).Trim();
        if (!useWssExternal || host.Length == 0)
            return null;

        // Remove esquema/porta/path caso o campo traga "https://host/" — o host
        // externo do NATS é sempre host[:porta].
        if (host.Contains("://"))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var parsed))
                return null;
            host = parsed.Host;
        }

        return Normalize($"wss://{host.TrimEnd('/')}/nats/");
    }
}
