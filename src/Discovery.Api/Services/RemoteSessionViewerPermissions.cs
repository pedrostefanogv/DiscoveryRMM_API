using Discovery.Core.Enums;

namespace Discovery.Api.Services;

/// <summary>
/// Permissões NATS do VIEWER de uma sessão remota (JWT scoped) e cálculo do TTL.
///
/// Fonte única: usado tanto por GET /nats-credentials quanto pela resposta de
/// POST /remote-sessions (credenciais embutidas no start, 2026-10-27). Duplicar
/// as listas já causou -ERR Permissions Violation quando um subject novo era
/// adicionado em só um dos caminhos.
///
/// <b>Restrições por sessão (RemoteSessionRights)</b> são aplicadas AQUI: a
/// credencial do viewer simplesmente não recebe os subjects de input, terminal,
/// arquivos, clipboard ou desktop conforme a política. A imposição fica no broker
/// NATS, então não depende de o cliente web se comportar — um viewer malicioso ou
/// adulterado que tente publicar em <c>...input</c> recebe -ERR Permissions Violation.
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

    /// <summary>PUB/SUB permitidos para o viewer da sessão (sem restrições).</summary>
    public static (string[] Publish, string[] Subscribe) Build(string natsSubject)
        => Build(natsSubject, RemoteSessionRights.None);

    /// <summary>
    /// PUB/SUB permitidos para o viewer, respeitando as restrições da sessão.
    ///
    /// Cada restrição remove os subjects correspondentes das DUAS listas (pub e sub):
    /// remover só do pub deixaria o viewer recebendo dados que não deveria (ex.: ver
    /// o terminal sem poder digitar ainda é leitura de dados do cliente), e remover
    /// só do sub faria o viewer tentar operar e falhar com -ERR.
    /// </summary>
    public static (string[] Publish, string[] Subscribe) Build(string natsSubject, RemoteSessionRights rights)
    {
        var viewOnly = rights.HasFlag(RemoteSessionRights.ViewOnly);
        var limitedInput = rights.HasFlag(RemoteSessionRights.LimitedInput);
        var noTerminal = rights.HasFlag(RemoteSessionRights.NoTerminal);
        var noFiles = rights.HasFlag(RemoteSessionRights.NoFiles);
        var noDesktop = rights.HasFlag(RemoteSessionRights.NoDesktop);
        var noEvents = rights.HasFlag(RemoteSessionRights.NoEvents);

        var pub = new List<string>
        {
            $"{natsSubject}.ack",
            // Controle do viewer (keyframe ao voltar para a aba, liveness).
            $"{natsSubject}.control",
        };

        // Input de teclado/mouse: bloqueado em view-only e input limitado.
        // (LimitedInput deixa o teclado para o agente decidir; aqui o gate grosso é
        // não conceder o canal de input de desktop.)
        if (!viewOnly && !limitedInput)
            pub.Add($"{natsSubject}.input");

        if (!noTerminal)
        {
            pub.Add($"{natsSubject}.term.in");
            // Autocompletar do terminal (viewer -> agent). Sem PUB o NATS rejeita
            // com -ERR e derruba o WebSocket.
            pub.Add($"{natsSubject}.completion.req");
        }

        if (!noFiles)
            pub.Add($"{natsSubject}.files.req");

        pub.Add($"{natsSubject}.proxy.req");
        pub.Add($"{natsSubject}.proc.req");

        if (!viewOnly)
            pub.Add($"{natsSubject}.clipboard.req");

        var sub = new List<string>
        {
            $"{natsSubject}.frame",
            $"{natsSubject}.frame.frag",
            $"{natsSubject}.monitors",
            $"{natsSubject}.stats",
            $"{natsSubject}.control",
        };

        // Cursor revela a posição do mouse do operador na máquina do cliente.
        if (!noDesktop)
        {
            sub.Add($"{natsSubject}.cursor");
            sub.Add($"{natsSubject}.cursor.img");
        }

        if (!noEvents)
            sub.Add($"{natsSubject}.event");

        if (!viewOnly)
            sub.Add($"{natsSubject}.clipboard");

        if (!noTerminal)
        {
            sub.Add($"{natsSubject}.term.out");
            sub.Add($"{natsSubject}.term.ready");
            sub.Add($"{natsSubject}.completion.res");
        }

        if (!noFiles)
        {
            sub.Add($"{natsSubject}.files.ready");
            sub.Add($"{natsSubject}.files.resp");
            sub.Add($"{natsSubject}.files.progress");
        }

        sub.Add($"{natsSubject}.proxy.resp");
        sub.Add($"{natsSubject}.proc.resp");
        sub.Add($"{natsSubject}.proc.ready");

        return (pub.ToArray(), sub.ToArray());
    }
}
