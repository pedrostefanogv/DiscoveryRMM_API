namespace Discovery.Core.Enums;

public enum AgentUpdateEventType
{
    CheckStarted = 0,
    CheckCompleted = 1,
    UpdateAvailable = 2,
    DownloadStarted = 3,
    DownloadCompleted = 4,
    DownloadFailed = 5,
    InstallStarted = 6,
    InstallSucceeded = 7,
    InstallFailed = 8,
    Deferred = 9,
    RollbackStarted = 10,
    RollbackSucceeded = 11,
    RollbackFailed = 12
}
