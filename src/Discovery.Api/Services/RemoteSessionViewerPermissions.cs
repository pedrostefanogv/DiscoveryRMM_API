namespace Discovery.Api.Services;

/// <summary>
/// Permissões NATS do VIEWER de uma sessão remota (JWT scoped) e cálculo do TTL.
///
/// Fonte única: usado tanto por GET /nats-credentials quanto pela resposta de
/// POST /remote-sessions (credenciais embutidas no start, 2026-10-27). Duplicar
/// as listas já causou -ERR Permissions Violation quando um subject novo era
/// adicionado em só um dos caminhos.
/// </summary>
public static class RemoteSessionViewerPermissions
{
    /// <summary>TTL da credencial do viewer: tempo restante da sessão + 10 min
    /// (mínimo 30 min). Antes era fixo em 30 min e expirava no meio do uso.</summary>
    public static int TtlMinutes(DateTime expiresAtUtc)
    {
        var remaining = expiresAtUtc - DateTime.UtcNow;
        return remaining > TimeSpan.Zero
            ? (int)Math.Ceiling(remaining.Add(TimeSpan.FromMinutes(10)).TotalMinutes)
            : 30;
    }

    /// <summary>PUB/SUB permitidos para o viewer da sessão.</summary>
    public static (string[] Publish, string[] Subscribe) Build(string natsSubject)
    {
        var pub = new[]
        {
            $"{natsSubject}.input",
            $"{natsSubject}.ack",
            $"{natsSubject}.term.in",
            // Terminal remoto: consulta de autocompletar (viewer -> agent).
            // O viewer publica completion.req e recebe completion.res; sem a
            // permissao de PUB o NATS rejeita com -ERR e derruba o WebSocket.
            $"{natsSubject}.completion.req",
            $"{natsSubject}.files.req",
            $"{natsSubject}.proxy.req",
            $"{natsSubject}.proc.req",
            $"{natsSubject}.clipboard.req",
            // Controle do viewer (keyframe ao voltar para a aba, liveness).
            $"{natsSubject}.control",
        };

        var sub = new[]
        {
            $"{natsSubject}.frame",
            $"{natsSubject}.frame.frag",
            $"{natsSubject}.cursor",
            $"{natsSubject}.cursor.img",
            $"{natsSubject}.monitors",
            $"{natsSubject}.event",
            $"{natsSubject}.clipboard",
            $"{natsSubject}.term.out",
            $"{natsSubject}.term.ready",
            $"{natsSubject}.stats",
            $"{natsSubject}.completion.res",
            $"{natsSubject}.files.ready",
            $"{natsSubject}.files.resp",
            $"{natsSubject}.files.progress",
            $"{natsSubject}.proxy.resp",
            $"{natsSubject}.proc.resp",
            $"{natsSubject}.proc.ready",
            $"{natsSubject}.control",
        };

        return (pub, sub);
    }
}
