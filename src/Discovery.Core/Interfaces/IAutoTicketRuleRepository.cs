using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IAutoTicketRuleRepository
{
    Task<AutoTicketRule?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<AutoTicketRule>> GetAllAsync(
        AutoTicketScopeLevel? scopeLevel = null,
        Guid? scopeId = null,
        bool? isEnabled = null,
        string? alertCode = null);
    Task<AutoTicketRule> CreateAsync(AutoTicketRule rule);
    Task<AutoTicketRule> UpdateAsync(AutoTicketRule rule);
    Task<bool> DeleteAsync(Guid id);
}