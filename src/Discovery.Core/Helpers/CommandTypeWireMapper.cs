using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

public static class CommandTypeWireMapper
{
    public static string ToWireValue(CommandType commandType)
    {
        return commandType switch
        {
            CommandType.Shell => "shell",
            CommandType.PowerShell => "powershell",
            CommandType.Script => "script",
            CommandType.FileTransfer => "filetransfer",
            CommandType.SystemInfo => "systeminfo",
            CommandType.Restart => "restart",
            CommandType.Shutdown => "shutdown",
            CommandType.Update => "update",
            CommandType.RemoteDebug => "remotedebug",
            CommandType.ShowPsadtAlert => "showpsadtalert",
            CommandType.Notification => "notification",
            _ => commandType.ToString().ToLowerInvariant()
        };
    }

    public static bool IsSpecialCommand(CommandType commandType)
    {
        return commandType is CommandType.RemoteDebug
            or CommandType.ShowPsadtAlert
            or CommandType.Notification
            or CommandType.Update;
    }
}