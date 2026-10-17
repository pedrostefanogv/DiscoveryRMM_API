using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Decisão de autorização scoped das gravações de terminal.
///
/// O atributo <c>RequirePermission(..., ScopeSource.AccessList)</c> só garante que o
/// usuário tem ALGUM acesso ao recurso Agents; esta policy é quem recorta o tenant:
/// libera acesso global ou permissão no cliente/site da sessão. Fica isolada do
/// controller para poder ser testada com fakes de <see cref="UserScopeAccess"/>.
/// </summary>
public static class TerminalRecordingAccessPolicy
{
    /// <summary>
    /// True quando o usuário pode acessar a gravação do tenant informado.
    /// Global passa direto; Client vale para o cliente da sessão; Site vale para o
    /// site da sessão (herança de Client já é aplicada pelo IPermissionService).
    /// </summary>
    public static bool Allows(UserScopeAccess? access, TerminalRecordingTenant tenant)
    {
        if (access is null)
            return false;

        if (access.HasGlobalAccess)
            return true;

        if (access.AllowedClientIds.Contains(tenant.ClientId))
            return true;

        return tenant.SiteId != Guid.Empty && access.AllowedSiteIds.Contains(tenant.SiteId);
    }
}
