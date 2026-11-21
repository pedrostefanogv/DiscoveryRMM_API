namespace Discovery.Core.Enums;

/// <summary>
/// Transporte usado pela sessão remota.
/// </summary>
public enum RemoteTransport
{
    /// <summary>
    /// Transporte direto (peer-to-peer) via WebRTC, para tela, túnel TCP/RDP e
    /// arquivos.
    ///
    /// NÃO é o default e exige ativação explícita na política herdável
    /// (<see cref="Configuration.RemoteAccessWebRtcPolicy"/>, chave
    /// <c>RemoteAccessWebRtcEnabled</c>): desligado, todo o tráfego continua pelo
    /// NATS. Quando o ICE falha e <c>FallbackToNats</c> está ligado, o agent cai
    /// automaticamente para NATS sem encerrar a sessão.
    ///
    /// HISTÓRICO: este valor foi marcado obsoleto quando o WebRTC foi removido
    /// (commit b8bcc90) por ser código morto no caminho de mídia. A reintrodução
    /// é aditiva e governada: o controle/liveness permanece no NATS, então o
    /// servidor não perde a capacidade de encerrar uma sessão com mídia P2P.
    ///
    /// O VALOR NUMÉRICO (0) É PRESERVADO: a coluna <c>transport</c> é persistida
    /// como int e sessões históricas o utilizam.
    ///
    /// ⚠️ ARMADILHA DE MANUTENÇÃO: sendo 0, este é o valor DEFAULT de um enum
    /// desserializado sem o campo "transport". Um pedido que omita o campo vira
    /// <c>Webrtc</c>, não <c>Nats</c>. Isso é SEGURO hoje porque o bloco "webrtc"
    /// do payload só é enviado quando a política herdável está efetiva
    /// (<c>RemoteAccessWebRtcPolicy.IsEffective</c>, que exige Enabled E kinds
    /// válidos) — e o agent revalida. Não troque a ORDEM deste membro nem
    /// remova essa checagem sem tratar o caso: qualquer um dos dois faria uma
    /// sessão sem "transport" explícito abrir peer connection por engano.
    /// </summary>
    Webrtc = 0,

    Nats = 1,
    Http = 2
}
