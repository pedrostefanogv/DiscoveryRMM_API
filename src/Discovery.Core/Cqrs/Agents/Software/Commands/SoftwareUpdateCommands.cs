using Discovery.Core.Cqrs;

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
