namespace Discovery.Infrastructure.Services;

/// <summary>
/// Pipeline da SAÍDA bruta do LLM antes de ela virar a resposta final do chat:
///   1. extrai as mensagens A2UI (blocos ```a2ui) e as remove do texto;
///   2. sanitiza vazamentos de tool call / marcações internas do texto restante.
///
/// A ORDEM é crítica e já foi a causa raiz de um bug em produção: o
/// <see cref="AiChatLeakSanitizer"/> tem um fallback (passo 5) que APAGA blocos
/// ```a2ui inteiros — pensado para caminhos que não passam pelo extractor. Quando
/// a sanitização era executada ANTES do <see cref="AiChatA2uiExtractor"/>, o bloco
/// já chegava removido e nenhuma mensagem A2UI era encontrada; o agent não recebia
/// o chunk "a2ui" e a interface desaparecia silenciosamente do chat (o usuário via
/// apenas o texto que anunciava o card, sem o card).
///
/// Centralizar aqui garante que os dois caminhos de streaming (StreamAsync e
/// StreamMultiRoundAsync) usem sempre a mesma ordem e que um teste de regressão
/// cubra a composição — e não apenas cada helper isolado.
/// </summary>
public static class AiChatOutputPipeline
{
    /// <summary>
    /// Processa o conteúdo bruto do LLM.
    /// </summary>
    /// <param name="content">Conteúdo bruto (tokens concatenados).</param>
    /// <returns>
    /// CleanContent: texto visível (sem A2UI, sem vazamentos).
    /// A2uiMessages: mensagens A2UI válidas, na ordem de emissão.
    /// LeaksRemoved: true quando a sanitização removeu algo.
    /// </returns>
    public static (string CleanContent, List<string> A2uiMessages, bool LeaksRemoved) Process(
        string content,
        Action<string>? onInvalidA2ui = null)
    {
        // 1) Extrai A2UI PRIMEIRO: o sanitizador removeria o bloco como fallback.
        var (contentWithoutInterface, extracted) = AiChatA2uiExtractor.Extract(content);

        // 2) Valida contra o catálogo do renderer. Interface inválida é descartada
        //    por inteiro (o usuário lê o texto) em vez de renderizar e falhar com
        //    "Não foi possível exibir a interface interativa gerada.".
        var (a2uiMessages, invalidReasons) = AiChatA2uiValidator.Validate(extracted);
        if (onInvalidA2ui != null)
        {
            foreach (var reason in invalidReasons)
                onInvalidA2ui(reason);
        }

        // 3) Sanitiza o restante (DSML, invokes textuais, ações A2UI cruas).
        var (clean, leaksRemoved) = AiChatLeakSanitizer.Sanitize(contentWithoutInterface);

        return (clean, a2uiMessages, leaksRemoved);
    }
}
