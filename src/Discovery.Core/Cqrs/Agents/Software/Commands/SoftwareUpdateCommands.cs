using Discovery.Core.Cqrs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Agents.Software.Commands;

/// <summary>
/// Dispara a atualização de um aplicativo instalado no agent. O servidor
/// resolve o item no inventário (packageId/origem reportados pelo agent) e envia
/// o comando dedicado <see cref="Enums.CommandType.SoftwareUpdate"/>, que o
/// agent executa pelo fluxo nativo de pacotes (winget/chocolatey) — com P2P e
/// switches silenciosos do catálogo.
///
/// Quando o pacote não está aprovado na loja para o escopo, o comando só é
/// despachado com <paramref name="ConfirmUnapproved"/> = true (confirmação
/// explícita do operador).
/// </summary>
public sealed record UpdateAgentSoftwareCommand(
    Guid AgentId,
    Guid InventoryId,
    bool ConfirmUnapproved = false
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Desinstala um aplicativo instalado no agent a partir do inventário. O agent
/// resolve a melhor estratégia: gerenciador de pacotes (winget/choco) → MSI
/// pelo ProductCode → UninstallString do registro. Quando nenhuma é possível,
/// o agent devolve erro claro. Desinstalação não é sujeita à política da loja.
/// </summary>
public sealed record UninstallAgentSoftwareCommand(
    Guid AgentId,
    Guid InventoryId
) : ICommand<Result<VoidResult>>;

/// <summary>
/// Instala no agent um aplicativo do catálogo da loja que ainda não está na
/// máquina (portanto não há item de inventário: a identidade vem do próprio
/// catálogo — packageId + tipo de instalação). Envia o comando dedicado
/// <see cref="Enums.CommandType.SoftwareInstall"/>, executado pelo agent via
/// fluxo nativo de pacotes (winget/chocolatey) ou pelo instalador publicado no
/// catálogo custom.
///
/// <paramref name="InstallationType"/> preserva <c>Custom</c> — diferente de
/// update/uninstall, o tipo faz parte da identidade do pacote.
///
/// Quando o pacote não está aprovado na loja para o escopo, o comando só é
/// despachado com <paramref name="ConfirmUnapproved"/> = true (confirmação
/// explícita do operador).
/// </summary>
public sealed record InstallAgentSoftwareCommand(
    Guid AgentId,
    string PackageId,
    AppInstallationType InstallationType,
    bool ConfirmUnapproved = false
) : ICommand<Result<VoidResult>>;
