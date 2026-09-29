namespace Discovery.Core.Enums;

public enum AgentLabelApplyMode
{
    ApplyOnly = 0,
    ApplyAndRemove = 1,
    Manual = 2,

    /// <summary>
    /// Remove labels MANUAIS que casem com o alvo enquanto a condicao for verdadeira.
    /// Nao adiciona nada e nunca toca labels automaticas/protegidas. Ver
    /// <see cref="AgentLabelLabelMatch"/> para o alvo (exato/prefixo/regex).
    /// </summary>
    Remove = 3
}
