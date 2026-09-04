using System.Text.Json.Serialization;
using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.P2p.Commands;

/// <summary>
/// Comando para solicitar pré-carga P2P de packages em um agent específico.
/// O agent decide se executa agora ou adia conforme o score de eleição local
/// (o melhor candidato baixa primeiro; os demais aguardam o artifact aparecer
/// no gossip e replicam via re-seed, sem ir à internet).
/// Handler: Discovery.Infrastructure Cqrs.Agents.CommandHandlers.
/// </summary>
public sealed record RequestP2pPreloadCommand(
    Guid AgentId,
    [property: JsonPropertyName("packages")] List<PreloadPackageRequest> Packages,
    [property: JsonPropertyName("action")] string? Action
) : ICommand<Result<VoidResult>>;

public sealed record PreloadPackageRequest(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("actionType")] string? ActionType
);
