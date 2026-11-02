using Discovery.Core.Cqrs;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Detector de falha de carregamento de assembly/tipo causada por atualização do
/// runtime .NET DURANTE a execução do processo (incidente de 2026-11-02: "apt upgrade"
/// trocou 10.0.13 → 10.0.14 e removeu o diretório antigo enquanto a API rodava).
/// Qualquer assembly carregada sob demanda depois disso falha com FileNotFoundException
/// (ex.: System.Runtime.Serialization.Primitives pedida pela Fido2NetLib).
/// A correção real é reiniciar o serviço; o código só precisa sinalizar isso
/// (RuntimeStale → HTTP 503) em vez de responder "Internal" genérico.
/// </summary>
public static class RuntimeStaleFailure
{
    /// <summary>Mensagem acionável para o log do operador.</summary>
    public const string OperatorHint =
        "O runtime .NET foi atualizado sob o processo em execução (assembly não pôde ser carregada). " +
        "Reinicie o serviço discovery-api para restaurar o funcionamento.";

    /// <summary>Mensagem em pt-BR devolvida ao usuário final.</summary>
    public const string UserMessage =
        "Serviço temporariamente indisponível devido a manutenção do servidor. " +
        "Tente novamente em instantes.";

    /// <summary>
    /// Exceções de carregamento de assembly/tipo. Percorre a cadeia de InnerException
    /// porque o serializer da Fido2NetLib costuma encapsular a falha original.
    /// </summary>
    public static bool IsRuntimeStale(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // FileLoadException/TypeLoadException/BadImageFormatException sempre indicam
            // problema de carga de assembly/tipo.
            if (current is FileLoadException or TypeLoadException or BadImageFormatException)
                return true;

            // FileNotFoundException e ambigua: resolve assembly inexistente OU arquivo de
            // dados ausente. Só classificamos como runtime obsoleto quando a mensagem é de
            // carga de assembly (falha de resolução de assembly não traz FileName).
            if (current is FileNotFoundException fnf && LooksLikeAssemblyLoadFailure(fnf))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Diferencia "não consegui carregar a assembly X" (runtime trocado sob o processo)
    /// de "não achei o arquivo Y" (defeito/arquivo de dados ausente), para não rotular
    /// um bug real como indisponibilidade temporária.
    /// </summary>
    private static bool LooksLikeAssemblyLoadFailure(FileNotFoundException ex)
    {
        // .NET usa este texto quando a resolução de assembly falha.
        return ex.Message.Contains("Could not load file or assembly", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Could not load the file or assembly", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Erro de domínio correspondente (código "RuntimeStale").</summary>
    public static Error CreateError() => Error.RuntimeStale(UserMessage);
}