using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Ordem de execução da tarefa de automação (0–99). O agent executa as tarefas
/// da policy em ordem crescente; no empate roda primeiro a tarefa atualizada há
/// mais tempo. O default depende do tipo de ação, para que um pacote seja
/// instalado antes de scripts/comandos sem configuração manual.
/// </summary>
public static class AutomationTaskExecutionOrder
{
    public const int Min = 0;
    public const int Max = 99;
    public const int Fallback = 50;

    /// <summary>
    /// Default por tipo de ação: instalar (10) → instalar/atualizar e atualizar
    /// (20) → remover (30) → script (40) → comando personalizado (50).
    /// </summary>
    public static int DefaultFor(AutomationTaskActionType actionType) => actionType switch
    {
        AutomationTaskActionType.InstallPackage => 10,
        AutomationTaskActionType.UpdateOrInstallPackage => 20,
        AutomationTaskActionType.UpdatePackage => 20,
        AutomationTaskActionType.RemovePackage => 30,
        AutomationTaskActionType.RunScript => 40,
        AutomationTaskActionType.CustomCommand => 50,
        _ => Fallback
    };

    /// <summary>Valor efetivo na criação: usa o pedido do operador ou o default do tipo de ação.</summary>
    public static int ResolveForCreate(int? requested, AutomationTaskActionType actionType)
    {
        var value = requested ?? DefaultFor(actionType);
        Validate(value);
        return value;
    }

    /// <summary>Garante que a ordem está dentro da faixa aceita (0–99).</summary>
    public static void Validate(int value)
    {
        if (value < Min || value > Max)
        {
            throw new InvalidOperationException($"ExecutionOrder must be between {Min} and {Max}.");
        }
    }
}
