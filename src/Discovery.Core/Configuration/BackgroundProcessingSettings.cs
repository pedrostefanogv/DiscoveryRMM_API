namespace Discovery.Core.Configuration;

/// <summary>
/// Configuração dos processamentos em segundo plano (métricas de atendente e
/// triagem de chamados por IA).
///
/// Persistida em ServerConfiguration.BackgroundProcessingSettingsJson (padrão)
/// e sobrescrita por ClientConfiguration.BackgroundProcessingSettingsJson
/// (campos ausentes/null herdam o global).
/// </summary>
public sealed class BackgroundProcessingSettings
{
    public TechnicianMetricsProcessingSettings Metrics { get; set; } = new();

    public TicketTriageProcessingSettings Triage { get; set; } = new();
}

/// <summary>
/// Ciclo das métricas por atendente. O job roda no tick mínimo (appsettings) e
/// cada cliente só é processado quando o seu intervalo vence.
/// </summary>
public sealed class TechnicianMetricsProcessingSettings
{
    /// <summary>Piso do intervalo (decisão de produto: nunca menos de 10 minutos).</summary>
    public const int MinimumIntervalMinutes = 10;
    public const int MaximumIntervalMinutes = 1440;
    public const int MinimumTickSeconds = 60;
    public const int MaximumTickSeconds = 3600;
    public const int MinimumStaleThresholdMinutes = 5;
    public const int MinimumBatchSize = 10;
    public const int MaximumBatchSize = 2000;
    public const int MaximumBatchesPerRun = 50;
    public const int MinimumRunSeconds = 10;
    public const int MaximumRunSeconds = 600;
    public const int MinimumWindowDays = 1;
    public const int MaximumWindowDays = 365;

    /// <summary>Liga/desliga o ciclo para o escopo.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Granularidade MÍNIMA de varredura do job (tick do Quartz), aplicada em
    /// runtime a partir desta configuração. Global — não é sobrescrevível por
    /// cliente (um trigger é global).
    /// </summary>
    public int TickSeconds { get; set; } = 300;

    /// <summary>Intervalo mínimo entre ciclos do mesmo cliente (min 10).</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>A partir de quantos minutos o snapshot é considerado vencido.</summary>
    public int StaleThresholdMinutes { get; set; } = 15;

    /// <summary>Janela (dias) usada nas taxas e médias.</summary>
    public int WindowDays { get; set; } = 90;

    /// <summary>Usuários processados por lote.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Teto de lotes por execução (mantém o ciclo curto).</summary>
    public int MaxBatchesPerRun { get; set; } = 4;

    /// <summary>Orçamento de tempo por execução, em segundos.</summary>
    public int MaxRunSeconds { get; set; } = 120;

    /// <summary>
    /// Calcula na hora apenas quem NUNCA teve snapshot. Snapshot vencido nunca é
    /// recalculado dentro de requisição — só pelo ciclo agendado.
    /// </summary>
    public bool BootstrapMissingSnapshots { get; set; } = true;
}

/// <summary>Ciclo de triagem por IA dos chamados (por cliente).</summary>
public sealed class TicketTriageProcessingSettings
{
    /// <summary>Piso do intervalo de lote (segundos) — permite cadência agressiva.</summary>
    public const int MinimumIntervalSeconds = 10;
    public const int MaximumIntervalSeconds = 86400;
    public const int MinimumTickSeconds = 10;
    public const int MaximumTickSeconds = 3600;
    public const int MinimumBatchSize = 1;
    public const int MaximumBatchSize = 200;
    public const int MaximumPerClientPerRun = 500;
    public const int MaximumAttempts = 10;
    public const int MaximumRetryAfterMinutes = 1440;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enfileira a triagem na abertura do chamado (fast lane). Com false o chamado
    /// entra apenas no processamento em lotes.
    /// </summary>
    public bool EnqueueOnCreate { get; set; } = true;

    /// <summary>
    /// Granularidade MÍNIMA de varredura do job (tick do Quartz), aplicada em
    /// runtime a partir desta configuração. Global — não é sobrescrevível por
    /// cliente (um trigger é global).
    /// </summary>
    public int TickSeconds { get; set; } = 20;

    /// <summary>Intervalo entre lotes do mesmo cliente (segundos).</summary>
    public int IntervalSeconds { get; set; } = 20;

    /// <summary>Itens por execução (limite global do processo).</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Cota por cliente por execução (fairness entre clientes).</summary>
    public int MaxPerClientPerRun { get; set; } = 10;

    /// <summary>Tentativas antes de aplicar o fallback determinístico.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Tempo máximo sem responsável antes da varredura de segurança.</summary>
    public int RetryAfterMinutes { get; set; } = 5;

    /// <summary>Espera mínima antes de triar no modo somente-lotes (segundos).</summary>
    public int BatchDelaySeconds { get; set; } = 0;
}
