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
            CommandType.WakeOnLan => "wakeonlan",
            CommandType.RemoteSessionStart => "remotesessionstart",
            CommandType.RemoteSessionStop => "remotesessionstop",
            CommandType.RemoteSessionQuality => "remotesessionquality",
            CommandType.RecordingStart => "recordingstart",
            CommandType.RecordingStop => "recordingstop",
            CommandType.P2pPreload => "p2ppreload",
            _ => commandType.ToString().ToLowerInvariant()
        };
    }

    public static bool IsSpecialCommand(CommandType commandType)
    {
        return commandType is CommandType.RemoteDebug
            or CommandType.ShowPsadtAlert
            or CommandType.Notification
            or CommandType.Update
            or CommandType.Restart
            or CommandType.Shutdown
            or CommandType.WakeOnLan
            or CommandType.RemoteSessionStart
            or CommandType.RemoteSessionStop
            or CommandType.RemoteSessionQuality
            or CommandType.RecordingStart
            or CommandType.RecordingStop
            or CommandType.P2pPreload;
    }
}