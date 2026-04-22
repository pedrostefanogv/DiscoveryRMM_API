using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Auditoria de mudanças em configurações. Registra todas as alterações
/// para fins de rastreabilidade e possível rollback.
/// </summary>
public class ConfigurationAudit
{
    public Guid Id { get; set; }
    
    /// <summary>Tipo de entidade (Server, Client ou Site)</summary>
    public ConfigurationEntityType EntityType { get; set; }
    
    /// <summary>ID da entidade modificada (ServerConfiguration.Id, ClientConfiguration.Id, etc)</summary>
    public Guid EntityId { get; set; }
    
    /// <summary>Nome do campo alterado</summary>
    public string FieldName { get; set; } = string.Empty;
    
    /// <summary>Valor anterior (JSON serialize se complexo)</summary>
    public string? OldValue { get; set; }
    
    /// <summary>Novo valor (JSON serialize se complexo)</summary>
    public string? NewValue { get; set; }
    
    /// <summary>Razão/comentário da mudança</summary>
    public string? Reason { get; set; }
    
    /// <summary>Usuário que fez a mudança</summary>
    public string? ChangedBy { get; set; }
    
    /// <summary>Data e hora da mudança</summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>IP de origem (opcional)</summary>
    public string? IpAddress { get; set; }
    
    /// <summary>Versão da entidade após a mudança</summary>
    public int EntityVersion { get; set; }
}
