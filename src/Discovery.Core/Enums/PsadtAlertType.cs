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
    Modal = 1
}
