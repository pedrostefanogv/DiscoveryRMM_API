using System.Globalization;

namespace Discovery.Core.Helpers;

/// <summary>
/// Classifica uma falha de instalação de patch em uma classe determinística
/// (WU-08). Antes, "falhou" era um texto livre e o job de auto-instalação
/// reprocessava QUALQUER linha Failed na próxima execução — sem contador, sem
/// espera e sem distinguir "disco cheio" de "update não aplicável", o que podia
/// virar laço infinito de instalação/falha.
///
/// Classes:
///  • <c>disk_space</c> — falta espaço; ação humana (limpar disco);
///  • <c>store_corrupt</c> — Component Store corrompido; ação humana (DISM);
///  • <c>needs_reboot</c> — instalação exige reinício antes de prosseguir;
///  • <c>permanent</c> — não aplicável/supersedido; NUNCA re-tentar sozinho;
///  • <c>transient</c> — rede/proxy/timeout/interrupção; re-tentar com backoff;
///  • <c>unknown</c> — sem sinal confiável; não re-tentar automaticamente.
///
/// A ordem da tabela importa: o primeiro token que casar vence.
/// </summary>
public static class PatchFailureClassifier
{
    public const string DiskSpace = "disk_space";
    public const string StoreCorrupt = "store_corrupt";
    public const string NeedsReboot = "needs_reboot";
    public const string Permanent = "permanent";
    public const string Transient = "transient";
    public const string Unknown = "unknown";

    /// <summary>Máximo de tentativas automáticas para falhas <c>transient</c>.</summary>
    public const int MaxTransientAttempts = 3;

    private static readonly (string Class, string[] Tokens)[] Rules =
    [
        (DiskSpace, ["0x80070070", "0x80070008", "disk space", "not enough disk", "espaço em disco", "espaco em disco"]),
        (StoreCorrupt, ["0x80073712", "0x800f081f", "0x800f0922", "component store", "componentes corrompidos"]),
        (NeedsReboot, ["0x8024001e", "0x80240017", "pending restart", "restart required", "reinício pendente", "reinicio pendente"]),
        (Permanent, ["0x8024200b", "not applicable", "não aplicável", "nao aplicavel", "superseded", "substituíd"]),
        (Transient, ["0x8024402c", "0x80072ee2", "0x80072efe", "timeout", "temporarily", "temporariamente", "interrupted", "interrompida", "network", "rede"]),
    ];

    /// <summary>Classifica a falha a partir do código estável e da mensagem.</summary>
    public static string Classify(string? errorCode, string? message, int? resultCode = null)
    {
        // Códigos do PRÓPRIO agente (preflight/erros tipados) vêm antes da
        // heurística de texto: são contrato estável, não mensagem traduzida.
        switch (errorCode?.Trim().ToLowerInvariant())
        {
            case "disk_space":
                return DiskSpace;
            case "service_stopped":
                // Serviço do Windows Update parado/desabilitado: exige ação
                // humana (GPO/administrador), não retry em laço.
                return Permanent;
            case "on_battery":
            case "timeout":
            case "busy":
            case "wua_unavailable":
                return Transient;
            case "unsupported":
                return Permanent;
            case "install_failed":
            case null:
            case "":
                break;
            default:
                // Código desconhecido: segue para a heurística de texto.
                break;
        }

        var haystack = string.Join(' ',
            errorCode ?? string.Empty,
            message ?? string.Empty,
            resultCode.HasValue ? "0x" + resultCode.Value.ToString("X8", CultureInfo.InvariantCulture) : string.Empty)
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(haystack))
            return Unknown;

        foreach (var (failureClass, tokens) in Rules)
        {
            foreach (var token in tokens)
            {
                if (haystack.Contains(token, StringComparison.Ordinal))
                    return failureClass;
            }
        }
        return Unknown;
    }

    /// <summary>
    /// Devolve quando a próxima tentativa deve ocorrer, ou null quando não há
    /// retry automático (permanente/desconhecida ou tentativas esgotadas).
    /// </summary>
    public static DateTime? NextAttemptAt(string failureClass, int failuresSoFar, DateTime now)
    {
        if (!string.Equals(failureClass, Transient, StringComparison.Ordinal))
            return null;
        if (failuresSoFar >= MaxTransientAttempts)
            return null;

        // Backoff: 1ª falha → 15 min; 2ª → 1 h; 3ª esgota (MaxTransientAttempts).
        var delay = failuresSoFar <= 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1);
        return now.Add(delay);
    }

    /// <summary>Uma linha só é re-tentável quando o backoff já venceu.</summary>
    public static bool IsRetryDue(string? failureClass, int failuresSoFar, DateTime? nextAttemptAt, DateTime now)
    {
        if (!string.Equals(failureClass, Transient, StringComparison.Ordinal))
            return false;
        if (failuresSoFar >= MaxTransientAttempts)
            return false;
        return nextAttemptAt is null || nextAttemptAt <= now;
    }
}
