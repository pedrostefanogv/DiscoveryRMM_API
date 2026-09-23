using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Software.Commands;

/// <summary>
/// Dispara a atualização de um aplicativo instalado no agent. O servidor
/// resolve o item no inventário de software do agent (installId + origem do
/// update reportados pelo próprio agent) e envia um comando PowerShell com a
/// chamada nativa do gerenciador de pacotes (winget upgrade / choco upgrade).
/// Não exige que o pacote esteja na política da loja: trata-se de atualizar um
/// app já instalado, sob demanda do operador.
/// </summary>
public sealed record UpdateAgentSoftwareCommand(Guid AgentId, Guid InventoryId) : ICommand<Result<VoidResult>>;
