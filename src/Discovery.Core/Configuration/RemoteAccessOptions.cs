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

    /// <summary>
    /// Tempo (s) que uma sessão pode ficar ACTIVE sem o viewer ter pedido
    /// credenciais/renovado. Cobre a janela entre o grant do consentimento e a
    /// conexão do viewer; se ninguém conectar, o sweeper fecha a sessão e manda
    /// stop ao agent (evita banner "suporte conectado" preso por 30min quando o
    /// console desiste depois do deadline). 0 = desabilita a limpeza.
    /// </summary>
    public int OrphanedSessionGraceSeconds { get; set; } = 120;

    public RemoteAccessNatsOptions Nats { get; set; } = new();
    public RemoteAccessLivenessOptions Liveness { get; set; } = new();
    public RemoteAccessProxyOptions Proxy { get; set; } = new();
    public RemoteAccessQualityOptions Quality { get; set; } = new();
    public RemoteAccessRecordingOptions Recording { get; set; } = new();

    /// <summary>Autorização do usuário do endpoint para sessões de desktop.</summary>
    public RemoteAccessConsentOptions Consent { get; set; } = new();
}

/// <summary>
/// Opções do fluxo de consentimento de acesso remoto (somente desktop).
/// </summary>
public class RemoteAccessConsentOptions
{
    /// <summary>
    /// Versão mínima do agente que entende o bloco "consent" no start. Um agente
    /// antigo ignoraria o bloco e abriria a sessão sem prompt — por isso a API
    /// recusa o start quando a política exige consentimento e o agente é antigo.
    /// </summary>
    public string MinimumAgentVersion { get; set; } = "1.3.0";

    /// <summary>Tolerância (s) após o deadline antes de o sweeper negar uma sessão pending.</summary>
    public int DecisionGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Janela (s) após o deadline em que a API ainda aceita a decisão do agente
    /// e em que o console continua aguardando em vez de falhar. Cobre o atraso
    /// entre o fim do contador no endpoint e a chegada do POST /consent
    /// (relógio do agente, timer do prompt e latência de rede). O sweeper só
    /// nega depois de <see cref="DecisionGraceSeconds"/>; mantenha este valor
    /// menor que o do sweeper para a decisão tardia nunca disputar com a negação.
    /// </summary>
    public int LateDecisionGraceSeconds { get; set; } = 15;
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

    /// <summary>
    /// Política de TETO DE BANDA do stream de tela. Controla o gate que PAUSA a
    /// captura quando o link não escoa (separado da qualidade, que apenas reduz
    /// a compressão). Ver RemoteAccessBandwidthOptions.
    /// </summary>
    public RemoteAccessBandwidthOptions Bandwidth { get; set; } = new();
}

/// <summary>
/// Política de teto de banda do stream de tela, por MODO de operação.
///
/// Por que o teto é separado da qualidade: a escada adaptativa REDUZ a
/// compressão quando a rede piora, mas continua produzindo frames no mesmo
/// ritmo. Num link que não escoa, isso só enche a fila — o frame entregue fica
/// cada vez mais velho (o operador clica onde a tela já não está). O teto de
/// banda é um SEGUNDO controle, que freia a PRODUÇÃO de frames.
///
/// Modos (espelham a UI de sessão remota):
///   - Auto:   teto ajustado automaticamente pelo agente a partir do bitrate
///             que o viewer efetivamente recebe. Parte de AutoStartKbps e varia
///             entre AutoMinKbps e AutoMaxKbps. 0 = desligado.
///   - Hd:     sem limite (0). Prioriza fluidez; a banda é responsabilidade da
///             rede e da escada de qualidade.
///   - Manual: teto fixo escolhido pelo operador, a partir de um preset.
///
/// O agente é a fonte da verdade da aplicação: ele recebe a política no start
/// e pode recebê-la de novo em runtime pelo canal .control.
/// </summary>
public class RemoteAccessBandwidthOptions
{
    /// <summary>
    /// Modo padrão do teto quando o operador não escolhe.
    /// "auto" | "hd" | "manual".
    /// </summary>
    public string DefaultMode { get; set; } = "auto";

    /// <summary>
    /// Teto padrão (kbps) para o modo manual quando nenhum preset é escolhido.
    /// 0 = sem teto.
    /// </summary>
    public int ManualDefaultKbps { get; set; } = 8000;

    /// <summary>
    /// Presets do modo manual, exibidos na UI como opções pré-definidas.
    /// A ordem aqui é a ordem exibida. Ver <see cref="BandwidthPreset"/>.
    /// </summary>
    public BandwidthPreset[] ManualPresets { get; set; } =
    [
        new() { Id = "low",    Label = "Baixa (1 Mbps)",   Kbps = 1000 },
        new() { Id = "medium", Label = "Média (4 Mbps)",   Kbps = 4000 },
        new() { Id = "high",   Label = "Alta (8 Mbps)",    Kbps = 8000 },
        new() { Id = "max",    Label = "Máxima (20 Mbps)", Kbps = 20000 },
        new() { Id = "unlimited", Label = "Sem limite",    Kbps = 0 }
    ];

    // ── Modo AUTO: o agente ajusta o teto sozinho ──

    /// <summary>Teto inicial do modo auto (kbps). 0 desliga o controle automático.</summary>
    public int AutoStartKbps { get; set; } = 8000;

    /// <summary>Piso do teto automático (kbps). Nunca desce abaixo disso.</summary>
    public int AutoMinKbps { get; set; } = 1000;

    /// <summary>Teto máximo do teto automático (kbps).</summary>
    public int AutoMaxKbps { get; set; } = 30000;

    /// <summary>
    /// Fração do teto que o viewer precisa receber para o agente considerar que
    /// há folga e SUBIR o teto. Ex.: 0.75 = se recebe &gt;75% do teto, sobe.
    /// </summary>
    public double AutoRaiseFactor { get; set; } = 0.75;

    /// <summary>
    /// Fração do teto abaixo da qual o agente considera aperto e DESCE o teto.
    /// Deve ser menor que <see cref="AutoRaiseFactor"/> para haver histerese.
    /// </summary>
    public double AutoLowerFactor { get; set; } = 0.6;

    /// <summary>Passo de ajuste do teto automático (kbps).</summary>
    public int AutoStepKbps { get; set; } = 2000;

    /// <summary>
    /// Teto absoluto imposto pelo servidor (kbps), aplicável a QUALQUER modo —
    /// inclusive HD. 0 = sem restrição do servidor. Útil para proteger a rede
    /// corporativa de um operador que escolha "sem limite".
    /// </summary>
    public int ServerMaxKbps { get; set; } = 0;
}

/// <summary>Opção pré-definida de teto de banda exibida na UI.</summary>
public class BandwidthPreset
{
    /// <summary>Identificador estável enviado no wire (ex.: "low").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Rótulo exibido ao operador.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Teto em kbps. 0 = sem limite.</summary>
    public int Kbps { get; set; }
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

    /// <summary>Gravação incremental do terminal (NDJSON).</summary>
    public RemoteAccessTerminalRecordingOptions Terminal { get; set; } = new();
}

/// <summary>
/// Opções da ingestão/gravação do terminal remoto. Os limites são aplicados
/// por sessão no servidor: o excedente é descartado e contabilizado em
/// dropped_frames, nunca propagado como exceção para o loop do NATS.
/// </summary>
public class RemoteAccessTerminalRecordingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Máximo de frames aceitos por segundo, por sessão. 0 = ilimitado.</summary>
    public int MaxFramesPerSecond { get; set; } = 30;

    /// <summary>Máximo de bytes aceitos por segundo, por sessão. 0 = ilimitado.</summary>
    public int MaxBytesPerSecond { get; set; } = 262_144;

    /// <summary>Tamanho máximo de uma gravação, em bytes. 0 = ilimitado.</summary>
    public long MaxRecordingBytes { get; set; } = 67_108_864;

    /// <summary>Intervalo de flush do writer e de persistência dos contadores (ms).</summary>
    public int FlushIntervalMs { get; set; } = 1_000;

    /// <summary>
    /// Diretório base do NDJSON. Vazio = Recording:Local:BasePath + "/terminal".
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Intervalo (s) da varredura que finaliza gravações cuja sessão remota já
    /// não está ativa. Sem ela, uma sessão encerrada sem frame de saída (agent
    /// caiu, StopRecording não chegou) manteria estado e arquivo abertos até o
    /// restart da API. 0 desabilita a varredura.
    /// </summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Idade mínima (s) do último frame para a gravação ser candidata à
    /// varredura. Evita consultar o banco a cada flush para sessões em uso.
    /// </summary>
    public int InactivityGraceSeconds { get; set; } = 30;

    /// <summary>
    /// Envia o NDJSON ao S3 no finalize quando
    /// <c>RemoteAccess:Recording:StorageProvider</c> == "S3". A gravação sempre
    /// acontece em disco local (append incremental); o upload é adicional e
    /// best-effort: falha preserva o arquivo local e grava LastError.
    /// </summary>
    public bool UploadToS3 { get; set; } = true;

    /// <summary>
    /// Mantém o NDJSON local depois de um upload bem-sucedido. Default true:
    /// o disco continua sendo a cópia de trabalho (download sem S3 e retenção
    /// local seguem funcionando).
    /// </summary>
    public bool KeepLocalCopyAfterUpload { get; set; } = true;

    /// <summary>Timeout de cada tentativa de upload ao S3, em segundos.</summary>
    public int UploadTimeoutSeconds { get; set; } = 30;

    /// <summary>Número máximo de tentativas de upload (1 = sem retry).</summary>
    public int UploadMaxAttempts { get; set; } = 3;
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
