namespace Discovery.Core.Configuration;

/// <summary>
/// Configuracao do debug remoto: canal unico de controle (ping-pong + setLevel)
/// e renovacao continua da sessao.
/// </summary>
public class RemoteDebugOptions
{
    public const string SectionName = "RemoteDebug";

    /// <summary>TTL padrao da sessao em minutos (janela de renovacao).</summary>
    public int DefaultTtlMinutes { get; set; } = 20;

    /// <summary>TTL minimo aceito.</summary>
    public int MinTtlMinutes { get; set; } = 2;

    /// <summary>TTL maximo de uma renovacao.</summary>
    public int MaxTtlMinutes { get; set; } = 120;

    /// <summary>
    /// Duracao maxima total da sessao (inclui renovacoes), em minutos.
    /// Definida na variavel de sistema REMOTE_SESSION_MAX_DURATION_HOURS do
    /// instalador (padrao 1h). 0 = ilimitado.
    /// </summary>
    public int MaxSessionDurationMinutes { get; set; } = 60;

    /// <summary>Cadencia do ping no canal de controle (segundos).</summary>
    public int PingIntervalSeconds { get; set; } = 5;

    /// <summary>Sinais perdidos antes de encerrar por ausencia do peer.</summary>
    public int MissedPingsBeforeClose { get; set; } = 3;

    /// <summary>
    /// Janela para o PRIMEIRO sinal do viewer. Nao e retrocompatibilidade: e a
    /// corrida real entre o comando de start e a popup conectar e pingar.
    /// </summary>
    public int InitialGraceSeconds { get; set; } = 60;

    /// <summary>Cadencia do keepalive HTTP do viewer (segundos).</summary>
    public int KeepAliveSeconds { get; set; } = 60;

    /// <summary>
    /// Servidor encerra a sessao apos este tempo sem keepalive (segundos).
    /// Resolve sessao presa quando o navegador morre sem avisar.
    /// </summary>
    public int KeepAliveTimeoutSeconds { get; set; } = 90;
}
