namespace Discovery.Core.Enums;

/// <summary>
/// Tipo de alerta PSADT exibido no endpoint do agent.
/// </summary>
public enum PsadtAlertType
{
    /// <summary>
    /// Toast que fecha automaticamente após o timeout configurado.
    /// </summary>
    Toast = 0,

    /// <summary>
    /// Modal bloqueante que exige confirmação do usuário para fechar.
    /// </summary>
    Modal = 1,

    /// <summary>
    /// Indicador de progresso para self-update do agent.
    /// Exibe barra de progresso sem interação do usuário.
    /// Campos esperados no payload: progressPercent (0-100), statusText, subtitle.
    /// </summary>
    UpdateProgress = 2
}
