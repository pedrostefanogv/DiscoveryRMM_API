namespace Meduza.Core.Enums;

public enum CommandStatus
{
    Pending = 0,
    Sent = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
    Timeout = 6
}
