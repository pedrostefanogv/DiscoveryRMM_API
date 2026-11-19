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
    Rdp = 6,

    /// <summary>
    /// Editor/visualizador do Registro do Windows (subjects .registry.req/.resp).
    ///
    /// Valor 7 é ADITIVO: não renumerar os anteriores. Sessões antigas gravadas
    /// com kind 0..6 continuam válidas. O agente espelha este valor em
    /// remotesession/session_tunnel_policy.go (RemoteSessionKindRegistry).
    /// </summary>
    Registry = 7
}
