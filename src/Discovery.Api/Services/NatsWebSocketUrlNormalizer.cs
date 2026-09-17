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
}
