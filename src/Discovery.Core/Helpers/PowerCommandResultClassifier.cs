using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Classificação do output de um comando de energia (restart/shutdown).
/// </summary>
/// <param name="Status">Status a persistir no AgentCommand.</param>
/// <param name="StatusCode">Código do contrato lido do output (executed, scheduled, ...); null quando genérico.</param>
/// <param name="NextRunAt">Quando o agente pretende executar (apenas deferred_to_window).</param>
public sealed record PowerResultClassification(
    CommandStatus Status,
    string? StatusCode,
    string? NextRunAt);

/// <summary>
/// Traduz o output do agente no status do comando. O output é a fonte de verdade
/// porque a confirmação antecipada do comando de energia usa ExitCode=0 mesmo
/// quando nada foi executado — exit code sozinho transformaria adiamento em
/// sucesso.
///
/// Contrato (JSON de uma linha):
///   {"status":"executed",...}                          -> Completed
///   {"status":"scheduled",...}                         -> Deferred (aceito, executa depois; terminal para não reentregar)
///   {"status":"deferred_by_user"|"deferred_to_window"} -> Deferred (nada executado)
///   {"status":"denied",...}                            -> Cancelled (usuário negou)
///   {"status":"failed",...}                            -> Failed
///
/// Agentes antigos mandam texto livre: o fallback é case-insensitive e cobre o
/// formato real de produção ("deferred_to_window nextRunAt=...").
///
/// ── Power MANUAL vs. janela de manutenção (correção 2026-11-13) ──
/// A sobrecarga que recebe <paramref name="commandType"/> trata o caso do comando
/// de energia INTERATIVO (Restart/Shutdown pedido pelo operador no console): para
/// ele, "deferred_to_window" NÃO é um desfecho válido e vira <c>Failed</c>.
///
/// Motivo (incidente real, servidor de homologação): um clique em
/// "Energia > Reiniciar" foi respondido pelo agente com
/// {"status":"deferred_to_window","nextRunAt":"2026-11-14T05:00:00Z"}. Como
/// <c>Deferred</c> é TERMINAL NEGATIVO — nunca é promovido a Completed
/// (CommandStatusTransitions), fica FORA da reentrega (CommandRepository) e é
/// APAGADO pela retenção sem executar (DataRetentionJob) — o operador via
/// "adiado para a janela de manutenção" e a máquina NUNCA reiniciava: um beco
/// sem saída silencioso. A janela de manutenção vale para AUTOMAÇÃO (patch,
/// pacote, auto-update); um pedido manual executa agora.
///
/// Assim, se um agente (tipicamente build legado) devolver deferral de janela
/// para um comando manual, o desfecho fica VISÍVEL como falha em vez de sumir.
/// "scheduled" (timer interno do agente) e "deferred_by_user" (o usuário adiou no
/// countdown) continuam <c>Deferred</c>: são adiamentos legítimos de power manual.
/// </summary>
public static class PowerCommandResultClassifier
{
    internal const string Executed = "executed";
    internal const string Scheduled = "scheduled";
    internal const string DeferredByUser = "deferred_by_user";
    internal const string DeferredToWindow = "deferred_to_window";
    internal const string Denied = "denied";
    internal const string Failed = "failed";

    private static readonly HashSet<string> KnownStatuses = new(StringComparer.Ordinal)
    {
        Executed, Scheduled, DeferredByUser, DeferredToWindow, Denied, Failed
    };

    // Formato real de produção (texto livre do agente antigo):
    // "deferred_to_window nextRunAt=2026-11-03T05:00:00Z". Captura o token
    // inteiro; aspas/vírgulas de borda são removidas no consumo.
    private static readonly Regex NextRunAtTextRegex = new(
        @"nextRunAt\s*=\s*(\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Classificação sem conhecer o tipo do comando (assinatura histórica):
    /// mantém o comportamento anterior — deferral de janela continua
    /// <c>Deferred</c>. Prefira a sobrecarga com <c>commandType</c> no fluxo de
    /// resultados dos agentes, que distingue power manual de automação.
    /// </summary>
    public static PowerResultClassification Classify(int exitCode, string? output)
        => Classify(exitCode, output, commandType: null);

    /// <summary>
    /// Classificação com o tipo do comando: para <see cref="CommandType.Restart"/>
    /// e <see cref="CommandType.Shutdown"/> (power INTERATIVO, pedido pelo
    /// operador) o deferral por janela de manutenção é inválido e vira
    /// <c>Failed</c> — ver o comentário da classe. Para os demais tipos (ou
    /// <paramref name="commandType"/> nulo) o resultado é idêntico ao da
    /// assinatura histórica.
    /// </summary>
    public static PowerResultClassification Classify(
        int exitCode,
        string? output,
        CommandType? commandType)
    {
        var (statusCode, nextRunAt) = TryReadJsonOutput(output);
        statusCode ??= DetectLegacyStatusCode(output);
        nextRunAt ??= TryReadNextRunAtFromText(output);

        // Comando de energia INTERATIVO (console -> agente): a janela de manutenção
        // não se aplica, logo um deferral de janela devolvido pelo agente é um
        // adiamento INVÁLIDO. Classificá-lo como Deferred (terminal negativo,
        // fora de reentrega, apagado pela retenção) esconderia o fato de que nada
        // foi executado e o comando nunca mais rodaria. Failed torna o desfecho
        // visível e acionável.
        var isManualPower = commandType is CommandType.Restart or CommandType.Shutdown;

        var status = statusCode switch
        {
            // "executed" com exit code != 0 é contraditório: o exit code real
            // manda. Evita mascarar a falha de um comando (não-power) que
            // imprimiu o contrato por acaso.
            Executed when exitCode != 0 => CommandStatus.Failed,
            Executed => CommandStatus.Completed,
            // O agente aceitou e vai executar sozinho (timer interno). Nada
            // executado ainda — e NÃO pode ser Running: Running entra em
            // GetRedeliveryCandidatesAsync (reentrega antes do timer disparar =
            // reboot/countdown duplicado) e em ExpireStaleAsync (viraria
            // Timeout). Deferred é terminal e comunica "aceito, execução
            // posterior" sem reabrir o comando.
            Scheduled => CommandStatus.Deferred,
            // deferred_by_user: adiamento escolhido pelo USUÁRIO no countdown —
            // legítimo também no power manual.
            DeferredByUser => CommandStatus.Deferred,
            // deferred_to_window: adiamento por JANELA. Só é legítimo para
            // automação; em power manual é inválido (agente legado) -> Failed.
            DeferredToWindow => isManualPower ? CommandStatus.Failed : CommandStatus.Deferred,
            // Usuário negou explicitamente: não é falha da máquina, é cancelamento.
            Denied => CommandStatus.Cancelled,
            Failed => CommandStatus.Failed,
            _ => exitCode == 0 ? CommandStatus.Completed : CommandStatus.Failed
        };

        return new PowerResultClassification(status, statusCode, nextRunAt);
    }

    /// <summary>True quando o desfecho não é uma execução concluída.</summary>
    public static bool IsNonExecution(CommandStatus status)
        => status is not CommandStatus.Completed;

    private static (string? StatusCode, string? NextRunAt) TryReadJsonOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (null, null);

        var candidate = ExtractJsonObject(output);
        if (candidate is null)
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null);

            string? status = null;
            string? nextRunAt = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;

                if (property.Name.Equals("status", StringComparison.OrdinalIgnoreCase))
                    status = property.Value.GetString()?.Trim().ToLowerInvariant();
                else if (property.Name.Equals("nextRunAt", StringComparison.OrdinalIgnoreCase))
                    nextRunAt = property.Value.GetString();
            }

            // "status" genérico (ex.: "completed"/"ok") não faz parte do contrato
            // de power: aí a decisão volta a ser por exit code.
            if (status is null || !KnownStatuses.Contains(status))
                status = null;

            return (status, nextRunAt);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Isola o primeiro objeto JSON do output. Agentes antigos misturam texto com
    /// JSON em volta; JsonDocument.Parse estrito falharia nesses casos.
    /// </summary>
    private static string? ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return output.Substring(start, end - start + 1);
    }

    private static string? DetectLegacyStatusCode(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        if (output.Contains(DeferredToWindow, StringComparison.OrdinalIgnoreCase))
            return DeferredToWindow;
        if (output.Contains("adiado pelo usuário", StringComparison.OrdinalIgnoreCase)
            || output.Contains("adiado pelo usuario", StringComparison.OrdinalIgnoreCase))
            return DeferredByUser;
        if (output.Contains("agendado silenciosamente", StringComparison.OrdinalIgnoreCase))
            return Scheduled;
        if (output.Contains("usuário negou", StringComparison.OrdinalIgnoreCase)
            || output.Contains("usuario negou", StringComparison.OrdinalIgnoreCase))
            return Denied;

        return null;
    }

    private static string? TryReadNextRunAtFromText(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = NextRunAtTextRegex.Match(output);
        return match.Success ? match.Groups[1].Value.Trim('"', ',', ';') : null;
    }
}