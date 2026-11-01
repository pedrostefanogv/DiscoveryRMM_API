using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Resolve a INTENÇÃO de atualização por pacote num escopo (Loja × automação),
/// sem executar nada. A UI usa para simular/avisar conflitos; o agent usará a
/// mesma resolução quando a execução do auto-update for habilitada.
/// </summary>
public interface IAppStoreUpdateIntentService
{
    Task<AppStoreUpdateIntentPageDto> GetUpdateIntentAsync(
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        AppInstallationType installationType,
        string? search = null,
        CancellationToken cancellationToken = default);
}
