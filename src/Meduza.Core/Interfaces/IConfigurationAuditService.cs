using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

/// <summary>
/// Serviço de auditoria para rastreabilidade de mudanças em configurações.
/// </summary>
public interface IConfigurationAuditService
{
    /// <summary>
    /// Registra uma mudança em uma configuração.
    /// </summary>
    Task LogChangeAsync(string entityType, Guid entityId, string fieldName, 
        string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null);
    
    /// <summary>
    /// Obtem histórico de mudanças de uma entidade.
    /// </summary>
    Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100);
    
    /// <summary>
    /// Obtem todas as mudanças de configuração recentes.
    /// </summary>
    Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000);
    
    /// <summary>
    /// Obtem mudanças feitas por um usuário específico.
    /// </summary>
    Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100);
    
    /// <summary>
    /// Obtem mudanças em um campo específico.
    /// </summary>
    Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName);
    
    /// <summary>
    /// Gera relatório de auditoria em período.
    /// </summary>
    Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate);
}
