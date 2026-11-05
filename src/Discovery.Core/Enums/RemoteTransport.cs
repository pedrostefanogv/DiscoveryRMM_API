namespace Discovery.Core.Enums;

/// <summary>
/// Transporte usado pela sessão remota.
/// </summary>
public enum RemoteTransport
{
    /// <summary>
    /// OBSOLETO — não selecionar em novas sessões.
    /// O transporte WebRTC foi removido do agente: qualquer pedido de "webrtc"
    /// (ou vazio) cai para NATS (ver
    /// Discovery/src/app/core/remotesession/manager.go, normalizeTransport).
    ///
    /// O VALOR NUMÉRICO (0) É PRESERVADO DE PROPÓSITO: a coluna <c>transport</c>
    /// é persistida como int e sessões históricas o utilizam.
    /// </summary>
    [Obsolete("WebRTC foi removido do agente; o transporte efetivo é sempre NATS. Valor mantido apenas para compatibilidade de dados históricos.")]
    Webrtc = 0,

    Nats = 1,
    Http = 2
}
