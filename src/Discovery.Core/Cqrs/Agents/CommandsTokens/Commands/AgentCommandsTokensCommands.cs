using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;

public sealed record SendAgentCommandCommand(Guid AgentId, string CommandType, string? Payload, DateTime? ExpiresAt) : ICommand<Result<AgentCommandDto>>;
public sealed record CreateAgentTokenCommand(Guid AgentId, string Name, DateTime? ExpiresAt) : ICommand<Result<AgentTokenDto>>;
public sealed record RevokeAgentTokenCommand(Guid AgentId, Guid TokenId) : ICommand<Result<VoidResult>>;
public sealed record RevokeAllAgentTokensCommand(Guid AgentId) : ICommand<Result<VoidResult>>;

// Result/ExitCode/ErrorMessage/CompletedAt são aditivos (opcionais) para a UI
// conseguir distinguir "executado" de "aceito/adiado/negado" — antes o DTO só
// expunha Status, e um comando deferido pelo agente aparecia como Completed.
public sealed record AgentCommandDto(
    Guid Id,
    string CommandType,
    string Status,
    DateTime CreatedAt,
    string? Result = null,
    int? ExitCode = null,
    string? ErrorMessage = null,
    DateTime? CompletedAt = null);
public sealed record AgentTokenDto(Guid Id, string Name, string TokenPrefix, DateTime CreatedAt, DateTime? ExpiresAt);