using Discovery.Core.Enums;

namespace Discovery.Api.Services;

/// <summary>
/// Par de subjects NATS do túnel WebSocket, escolhido pelo tipo da sessão.
///
/// Existem DOIS canais distintos e deliberadamente separados:
/// <list type="bullet">
/// <item><c>Rdp</c> → <c>.rdp.cleanpath.in/.out</c>. WebRDP no navegador via
/// RDCleanPath (IronRDP WASM). O wire é DER (ASN.1 SEQUENCE <c>0x30</c>) durante
/// o handshake e bytes RDP crus durante o relay.</item>
/// <item>Demais tipos → <c>.tunnel.in/.out</c>. Túnel multiplexado do proxy LAN:
/// frame binário <c>[0x01][op][4B streamId]</c> ou controle JSON iniciado por
/// <c>{</c> (<c>0x7B</c>).</item>
/// </list>
///
/// Por que não compartilhar um canal só: no agent, a sessão RDP roda o servidor
/// RDCleanPath enquanto o proxy LAN roda o túnel multiplexado. São máquinas de
/// estado diferentes, com codecs incompatíveis — um frame de um lado seria
/// descartado silenciosamente pelo outro.
/// </summary>
public readonly record struct TunnelSubjects(string In, string Out)
{
    /// <summary>Subjects do túnel para o tipo de sessão informado.</summary>
    public static TunnelSubjects For(RemoteSessionKind kind) => kind == RemoteSessionKind.Rdp
        ? Rdp
        : LanProxy;

    /// <summary>WebRDP no navegador (RDCleanPath / IronRDP WASM).</summary>
    public static TunnelSubjects Rdp => new("rdp.cleanpath.in", "rdp.cleanpath.out");

    /// <summary>Túnel multiplexado do proxy LAN.</summary>
    public static TunnelSubjects LanProxy => new("tunnel.in", "tunnel.out");
}
