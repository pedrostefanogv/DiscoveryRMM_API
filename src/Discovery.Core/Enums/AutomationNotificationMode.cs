namespace Discovery.Core.Enums;

/// <summary>
/// Como o usuario da maquina e notificado durante a execucao de uma automation task.
/// Sempre informativo — <see cref="Prompt"/> pede interacao (Continuar/Adiar), mas
/// nao e um fluxo de aprovacao: se o usuario nao responder, a acao padrao e continuar.
/// </summary>
public enum AutomationNotificationMode
{
    /// <summary>Nenhuma notificacao ao usuario.</summary>
    Silent = 0,

    /// <summary>Prompt Welcome do PSADT (Continuar/Adiar) antes de executar — comportamento anterior.</summary>
    Prompt = 1,

    /// <summary>Toast informativo do PSADT, sem interacao (nao exige autorizacao do usuario).</summary>
    Toast = 2
}
