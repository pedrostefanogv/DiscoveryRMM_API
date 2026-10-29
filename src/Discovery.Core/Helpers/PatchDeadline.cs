namespace Discovery.Core.Helpers;

/// <summary>
/// Deadline de instalação (WU-05, decisões D02/D03).
///
/// Regras aprovadas:
///  • D02: o produto NUNCA reinicia automaticamente. O deadline é sobre
///    INSTALAÇÃO, não sobre reboot.
///  • D03: quando o deadline vence, a janela de manutenção é AMPLIADA até o
///    deadline — o patch é instalado fora da janela recorrente, com registro
///    auditado ("janela estendida por deadline"). Não é instalação silenciosa.
///
/// O deadline é ancorado em <c>PublishedAt</c> (idade real do patch). Sem data
/// de publicação não há como calcular a idade: o deadline não se aplica
/// (fail-open para a JANELA, mas o deferral fail-closed de WU-04 continua
/// valendo — os dois são independentes).
/// </summary>
public static class PatchDeadline
{
    /// <summary>Deadline do patch, ou null quando não aplicável.</summary>
    public static DateTime? DeadlineAt(int deadlineDays, DateTime? publishedAt)
    {
        if (deadlineDays <= 0 || publishedAt is null)
            return null;
        var published = publishedAt.Value.Kind == DateTimeKind.Utc
            ? publishedAt.Value
            : publishedAt.Value.ToUniversalTime();
        return published.AddDays(deadlineDays);
    }

    /// <summary>Verdadeiro quando o deadline já venceu (instalação obrigatória).</summary>
    public static bool IsPastDeadline(int deadlineDays, DateTime? publishedAt, DateTime utcNow)
    {
        var deadline = DeadlineAt(deadlineDays, publishedAt);
        if (deadline is null)
            return false;
        var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        return now >= deadline.Value;
    }

    /// <summary>
    /// Janela efetiva: aberta pela recorrência OU ampliada por deadline vencido
    /// (D03). Devolve também o motivo, para log/auditoria.
    /// </summary>
    public static (bool Open, string Reason) EvaluateWindow(
        bool recurringWindowOpen,
        int deadlineDays,
        DateTime? publishedAt,
        DateTime utcNow)
    {
        if (recurringWindowOpen)
            return (true, "janela recorrente aberta");

        if (IsPastDeadline(deadlineDays, publishedAt, utcNow))
            return (true, "janela estendida por deadline vencido");

        return (false, "fora da janela e antes do deadline");
    }
}
