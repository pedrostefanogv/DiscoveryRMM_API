namespace Discovery.Core.Configuration;

using Discovery.Core.Enums;

/// <summary>
/// Opções de configuração para o sistema de logging automático
/// </summary>
public class AutomaticLoggingOptions
{
    /// <summary>
    /// Habilita o logging automático
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Nível mínimo de log a ser registrado (Debug, Info, Warning, Error, Fatal)
    /// </summary>
    public string MinimumLevel { get; set; } = "Warning";

    /// <summary>
    /// Níveis de log permitidos para registrar automaticamente
    /// </summary>
    public List<string> AllowedLevels { get; set; } = new()
    {
        "Debug", "Info", "Warning", "Error", "Fatal"
    };

    /// <summary>
    /// Habilita logging automático de exceções não tratadas
    /// </summary>
    public bool LogExceptions { get; set; } = true;

    /// <summary>
    /// Habilita logging automático de eventos de negócio (Controllers)
    /// </summary>
    public bool LogBusinessEvents { get; set; } = true;

    /// <summary>
    /// Habilita logging automático de operações de infraestrutura (Redis, NATS, DB)
    /// </summary>
    public bool LogInfrastructureOps { get; set; } = true;

    /// <summary>
    /// Padrões de URI a excluir do logging automático (regex patterns)
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "/health", "/metrics", "/swagger"
    };

    /// <summary>
    /// Converte a string MinimumLevel para enum LogLevel
    /// </summary>
    public LogLevel GetMinimumLevelEnum()
    {
        return MinimumLevel switch
        {
            "Trace" => LogLevel.Trace,
            "Debug" => LogLevel.Debug,
            "Info" => LogLevel.Info,
            "Warn" or "Warning" => LogLevel.Warn,
            "Error" => LogLevel.Error,
            "Fatal" => LogLevel.Fatal,
            _ => LogLevel.Warn
        };
    }

    /// <summary>
    /// Verifica se um nível de log deve ser registrado
    /// </summary>
    public bool IsLevelEnabled(LogLevel level)
    {
        if (!Enabled) return false;

        var minimumLevel = GetMinimumLevelEnum();
        return level >= minimumLevel;
    }
}
