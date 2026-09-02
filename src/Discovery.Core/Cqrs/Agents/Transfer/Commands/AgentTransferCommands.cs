using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Transfer.Commands;

public sealed record TransferAgentCommand(Guid AgentId, Guid TargetSiteId, Guid UserId) : ICommand<Result<AgentTransferDto>>;
public sealed record BulkTransferAgentsCommand(IReadOnlyList<Guid> AgentIds, Guid TargetSiteId, Guid UserId) : ICommand<Result<AgentTransferDto>>;
public sealed record ValidateAgentTransferQuery(Guid AgentId, Guid TargetSiteId) : IQuery<Result<ValidateTransferResponseDto>>;

public sealed record AgentTransferDto(bool Valid, string? Message, Guid? NewSiteId);

/// <summary>
/// DTO para resposta de validação de transferência de agente.
/// Corresponde à interface ValidateTransferResponse do frontend.
/// </summary>
public sealed record ValidateTransferResponseDto(
    bool IsValid,
    IReadOnlyList<string> Messages,
    bool IsCrossClient,
    string? PreviousSiteName,
    string? TargetSiteName,
    string? PreviousClientName,
    string? TargetClientName
);