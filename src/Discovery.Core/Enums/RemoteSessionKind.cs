namespace Discovery.Core.Enums;

/// <summary>
/// Tipos de sessão remota.
/// </summary>
public enum RemoteSessionKind
{
    Screen = 0,
    Terminal = 1,
    Files = 2,
    Proxy = 3,
    All = 4,
    Processes = 5,

    /// <summary>Túnel RDP: o viewer conecta um cliente RDP local a um túnel
    /// WebSocket/NATS que alcança a porta 3389 do agente (via .tunnel.in/.tunnel.out).</summary>
    Rdp = 6
}
