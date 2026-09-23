namespace Discovery.Core.Enums;

public enum CommandType
{
    Shell = 0,
    PowerShell = 1,
    Script = 2,
    FileTransfer = 3,
    SystemInfo = 4,
    Restart = 5,
    Shutdown = 6,
    Update = 7,
    RemoteDebug = 8,
    ShowPsadtAlert = 9,
    Notification = 10,
    WakeOnLan = 11,
    RemoteSessionStart = 12,
    RemoteSessionStop = 13,
    RemoteSessionQuality = 14,
    RecordingStart = 15,
    RecordingStop = 16,
    P2pPreload = 17,
    // Controle de inicialização e tarefas agendadas (detalhe do agent)
    StartupItem = 18,
    ScheduledTask = 19,
    // Atualização de software instalado a partir do inventário (detalhe do
    // agent). Delegada ao fluxo nativo de pacotes do agent (winget/choco),
    // aproveitando P2P e switches silenciosos do catálogo.
    SoftwareUpdate = 20,
    // Desinstalação de software instalado a partir do inventário. O agent
    // resolve a estratégia: gerenciador de pacotes → MSI (ProductCode) →
    // UninstallString do registro.
    SoftwareUninstall = 21
}
