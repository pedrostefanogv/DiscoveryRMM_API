namespace Discovery.Core.Enums;

/// <summary>
/// Momento do toast informativo quando <see cref="AutomationNotificationMode.Toast"/>
/// esta selecionado.
/// </summary>
public enum AutomationToastTiming
{
    /// <summary>Toast exibido antes de iniciar a execucao.</summary>
    Before = 0,

    /// <summary>Toast exibido apos a conclusao (sucesso, falha ou reinicio pendente).</summary>
    After = 1
}
