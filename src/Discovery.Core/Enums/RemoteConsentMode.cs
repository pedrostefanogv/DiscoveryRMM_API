namespace Discovery.Core.Enums;

/// <summary>
/// Como o agente deve tratar a conexão de área de trabalho:
/// Off = sem aviso; NotifyOnly = avisa mas conecta; Prompt = solicita autorização.
/// </summary>
public enum RemoteConsentMode
{
    Off = 0,
    NotifyOnly = 1,
    Prompt = 2
}
