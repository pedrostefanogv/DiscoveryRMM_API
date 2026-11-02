using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAppApprovalRuleRepository
{
    Task<AppApprovalRule?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AppApprovalRule>> GetByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType);
    Task<IReadOnlyList<AppApprovalRule>> GetForInheritanceAsync(Guid clientId, Guid siteId, Guid agentId, AppInstallationType installationType);
    Task<AppApprovalRule?> GetByUniqueKeyAsync(
        AppApprovalScopeType scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        AppInstallationType installationType,
        string packageId);
    /// <summary>
    /// Lista TODAS as regras de um pacote, em qualquer escopo. Usado ao excluir
    /// um pacote custom para não deixar regras órfãs (que voltariam a valer se o
    /// mesmo packageId fosse recriado).
    /// </summary>
    Task<IReadOnlyList<AppApprovalRule>> GetByPackageAsync(AppInstallationType installationType, string packageId);

    /// <summary>
    /// TODAS as regras do tipo de instalação, em qualquer escopo. Usado pela
    /// limpeza de regras órfãs (pacote que não existe mais no catálogo).
    /// </summary>
    Task<IReadOnlyList<AppApprovalRule>> GetAllByInstallationTypeAsync(AppInstallationType installationType, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Listagem global não implementada neste double de teste.");

    Task<AppApprovalRule> CreateAsync(AppApprovalRule rule);
    Task UpdateAsync(AppApprovalRule rule);
    Task DeleteAsync(Guid id);
}
