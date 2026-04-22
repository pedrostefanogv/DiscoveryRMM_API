namespace Discovery.Core.Helpers;

public static class MeshCentralRights
{
    public const int EditMesh = 1;
    public const int ManageUsers = 2;
    public const int ManageComputers = 4;
    public const int RemoteControl = 8;
    public const int AgentConsole = 16;
    public const int ServerFiles = 32;
    public const int WakeDevice = 64;
    public const int SetNotes = 128;
    public const int RemoteViewOnly = 256;
    public const int NoTerminal = 512;
    public const int NoFiles = 1024;
    public const int NoAmt = 2048;
    public const int LimitedDesktop = 4096;
    public const int LimitedEvents = 8192;
    public const int ChatNotify = 16384;
    public const int UninstallAgent = 32768;
    public const int NoDesktop = 65536;
    public const int RemoteCommands = 131072;
    public const int PowerActions = 262144;
    public const int GuestSharing = 524288;
    public const int DeviceDetails = 1048576;
    public const int Relay = 2097152;

    public const int NonDeviceAssignableMask = EditMesh | ManageUsers | ManageComputers;

    public const int AllSupportedDeviceRightsMask = RemoteControl
        | AgentConsole
        | ServerFiles
        | WakeDevice
        | SetNotes
        | RemoteViewOnly
        | NoTerminal
        | NoFiles
        | NoAmt
        | LimitedDesktop
        | LimitedEvents
        | ChatNotify
        | UninstallAgent
        | NoDesktop
        | RemoteCommands
        | PowerActions
        | GuestSharing
        | DeviceDetails
        | Relay;

    public static int SanitizeDeviceRights(int rights)
    {
        if (rights < 0)
            return AllSupportedDeviceRightsMask;

        return rights & ~NonDeviceAssignableMask;
    }
}