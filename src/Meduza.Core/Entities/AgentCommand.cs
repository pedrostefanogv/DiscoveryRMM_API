using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

public class AgentCommand
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public CommandType CommandType { get; set; }
    public string Payload { get; set; } = string.Empty;
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public string? Result { get; set; }
    public int? ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
