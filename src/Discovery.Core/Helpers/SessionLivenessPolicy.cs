namespace Discovery.Core.Helpers;

/// <summary>
/// Politica compartilhada de liveness/renovacao de sessoes remotas.
/// Usada pelo debug remoto agora e reutilizavel pelas sessoes de tela/terminal.
/// </summary>
public static class SessionLivenessPolicy
{
    /// <summary>
    /// Calcula a nova expiracao de uma sessao renovada: agora + ttl, limitada
    /// pelo teto absoluto (startedAt + maxDurationMinutes).
    /// Retorna false quando o teto ja foi atingido (nao renova).
    /// </summary>
    public static bool TryClampRenewal(
        DateTime nowUtc,
        DateTime startedAtUtc,
        int ttlMinutes,
        int maxDurationMinutes,
        out DateTime expiresAtUtc)
    {
        var ttl = ttlMinutes > 0 ? TimeSpan.FromMinutes(ttlMinutes) : TimeSpan.FromMinutes(20);
        var next = nowUtc.Add(ttl);

        if (maxDurationMinutes > 0)
        {
            var maxExpiry = startedAtUtc.AddMinutes(maxDurationMinutes);
            if (nowUtc >= maxExpiry)
            {
                expiresAtUtc = maxExpiry;
                return false;
            }

            if (next > maxExpiry)
                next = maxExpiry;
        }

        expiresAtUtc = next;
        return true;
    }

    /// <summary>Calcula o teto absoluto da sessao (0 = ilimitado).</summary>
    public static DateTime? ResolveMaxExpiresAt(DateTime startedAtUtc, int maxDurationMinutes)
        => maxDurationMinutes > 0 ? startedAtUtc.AddMinutes(maxDurationMinutes) : null;
}
