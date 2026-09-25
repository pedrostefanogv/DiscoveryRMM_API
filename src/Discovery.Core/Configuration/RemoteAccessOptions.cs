namespace Discovery.Core.Configuration;

/// <summary>
/// Configuração de acesso remoto nativo (screen, terminal, files, proxy, gravação).
/// </summary>
public class RemoteAccessOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>TTL padrão de sessão em minutos.</summary>
    public int DefaultTtlMinutes { get; set; } = 30;

    /// <summary>Duração máxima total de uma sessão (inclui renovações). 0 = ilimitado.</summary>
    public int MaxSessionDurationMinutes { get; set; } = 120;

    /// <summary>Máximo de sessões simultâneas por agent (1 = uma sessão por vez, forçando sobreposição).</summary>
    public int MaxConcurrentSessionsPerAgent { get; set; } = 1;

    /// <summary>Máximo de sessões simultâneas por usuário.</summary>
    public int MaxConcurrentSessionsPerUser { get; set; } = 5;

    public RemoteAccessNatsOptions Nats { get; set; } = new();
    public RemoteAccessLivenessOptions Liveness { get; set; } = new();
    public RemoteAccessProxyOptions Proxy { get; set; } = new();
    public RemoteAccessQualityOptions Quality { get; set; } = new();
    public RemoteAccessRecordingOptions Recording { get; set; } = new();
}

public class RemoteAccessNatsOptions
{
    public int MaxPayloadBytes { get; set; } = 2097152;
    public string FrameSubjectPrefix { get; set; } = "remote.session";
    /// <summary>Chave de assinatura JWT para tokens NATS. Obrigatório. Use env var ou vault em produção.</summary>
    public string JwtSigningKey { get; set; } = string.Empty;
    /// <summary>Intervalo de verificação de sessões expiradas (segundos).</summary>
    public int ExpirationCheckIntervalSeconds { get; set; } = 15;
}

/// <summary>
/// Contrato de liveness (ping/pong) das sessoes de acesso remoto. O viewer
/// envia ping no subject .control e o agent responde pong; sem sinal por
/// MissedPingsBeforeClose * PingIntervalSeconds a sessao e encerrada. Os
/// mesmos valores vao no payload de start para o agent.
/// </summary>
public class RemoteAccessLivenessOptions
{
    /// <summary>Cadencia do ping do viewer (segundos).</summary>
    public int PingIntervalSeconds { get; set; } = 5;

    /// <summary>Quantos intervalos sem sinal encerram a sessao.</summary>
    public int MissedPingsBeforeClose { get; set; } = 3;

    /// <summary>Janela de partida para o primeiro sinal do viewer (segundos).</summary>
    public int InitialGraceSeconds { get; set; } = 60;

    /// <summary>
    /// Tolerancia extra apos a expiracao antes de o sweeper encerrar a sessao
    /// (cobre atraso do ultimo /renew). Segundos.
    /// </summary>
    public int KeepAliveTimeoutSeconds { get; set; } = 300;

    /// <summary>Intervalo do sweeper de sessoes expiradas (segundos).</summary>
    public int SweepIntervalSeconds { get; set; } = 15;
}

public class RemoteAccessProxyOptions
{
    /// <summary>Allowlist vazia por padrão (bloqueio total inicial).</summary>
    public string[] DefaultAllowlist { get; set; } = [];
    public int[] AllowedPorts { get; set; } = [];
    public int MaxResponseBytes { get; set; } = 10485760;
}

public class RemoteAccessQualityOptions
{
    public string DefaultProfile { get; set; } = "high";
    public bool AdaptiveEnabled { get; set; } = true;
    public int AdaptiveIntervalSeconds { get; set; } = 5;
    public int AdaptiveHysteresisSeconds { get; set; } = 15;
    public int MinFps { get; set; } = 5;
    public int MaxFps { get; set; } = 30;
    public string DefaultCodec { get; set; } = "auto";
    public AdaptiveThresholds Thresholds { get; set; } = new();
}

public class AdaptiveThresholds
{
    public double HighLatencyMs { get; set; } = 300;
    public double LowLatencyMs { get; set; } = 50;
    public double LowBandwidthKbps { get; set; } = 500;
    public double HighBandwidthKbps { get; set; } = 5000;
}

public class RemoteAccessRecordingOptions
{
    public bool Enabled { get; set; } = true;
    public bool DefaultOn { get; set; } = false;
    public string StorageProvider { get; set; } = "Local";

    public RemoteAccessRecordingLocalOptions Local { get; set; } = new();
    public RemoteAccessRecordingS3Options S3 { get; set; } = new();
    public RemoteAccessRecordingRetentionOptions Retention { get; set; } = new();
    public RemoteAccessRecordingFormatOptions Format { get; set; } = new();
}

public class RemoteAccessRecordingLocalOptions
{
    public string BasePath { get; set; } = "/var/discovery/recordings";
    public int MaxDiskUsageGb { get; set; } = 50;
}

public class RemoteAccessRecordingS3Options
{
    public string Endpoint { get; set; } = "https://s3.amazonaws.com";
    public string Bucket { get; set; } = "discovery-rmm-recordings";
    public string Region { get; set; } = "us-east-1";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public bool UsePathStyle { get; set; } = false;
    public int PresignTtlMinutes { get; set; } = 15;
}

public class RemoteAccessRecordingRetentionOptions
{
    public int DefaultDays { get; set; } = 30;
    public int MaxDays { get; set; } = 90;
    public bool AutoDeleteExpired { get; set; } = true;
}

public class RemoteAccessRecordingFormatOptions
{
    public string Container { get; set; } = "Auto";
    public string VideoCodec { get; set; } = "Source";
}
